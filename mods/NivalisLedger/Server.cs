using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisLedger;

// The web page's server, on this machine only (127.0.0.1). Read-only except cost edits, which need this session's token:
// it's written into the page the server sends, so a page from anywhere else can't make changes. Requests must be
// addressed to localhost by name (blocks DNS rebinding).
//   GET  /             the page
//   GET  /api/state    the latest snapshot (JSON)
//   GET  /api/stream   the snapshot each time it changes (server-sent events)
//   POST /api/cost     {ingredient, unit}            a unit cost override for plates (unit null clears)
//   POST /api/linecost {venue, ingredient, text}     "10@330", "10@3.30" or "@3.30": reprice unconfirmed batches
//   POST /api/batch    {venue, ingredient, id, unit} reprice one batch
internal static class Server
{
    static TcpListener listener;
    static string token;
    static string page;
    static int port;

    internal static string Url => $"http://localhost:{port}/";

    internal static bool Start(int listenPort)
    {
        port = listenPort;
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NivalisLedger.index.html"))
        using (var r = new StreamReader(s))
            page = r.ReadToEnd().Replace("{{TOKEN}}", token);
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"Ledger: can't listen on port {port} ({e.Message}); change [Web] Port");
            return false;
        }
        new Thread(Accept) { IsBackground = true, Name = "NivalisLedger" }.Start();
        return true;
    }

    static void Accept()
    {
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { return; }
            new Thread(() => Handle(client)) { IsBackground = true }.Start();
        }
    }

    static void Handle(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 10000;
                var (method, path, headers, body) = Read(stream);
                if (method == null) return;
                string query = null;
                int q = path.IndexOf('?');
                if (q >= 0) { query = path[(q + 1)..]; path = path[..q]; }
                headers.TryGetValue("host", out var host);
                if (host == null || !(host.StartsWith("localhost:") || host.StartsWith("127.0.0.1:")))
                {
                    Send(stream, 403, "text/plain", "forbidden");
                    return;
                }
                if (method == "GET" && (path == "/" || path == "/index.html")) Send(stream, 200, "text/html; charset=utf-8", page);
                else if (method == "GET" && path == "/api/state") Send(stream, 200, "application/json", Ledger.Snapshot);
                else if (method == "GET" && path == "/api/stream") Stream(stream, QueryValue(query, "client"));
                else if (method == "POST")
                {
                    if (!headers.TryGetValue("x-ledger-token", out var t) || t != token) { Send(stream, 403, "text/plain", "bad token"); return; }
                    Send(stream, 200, "application/json", path == "/api/view" ? SetView(body) : Change(path, body));
                }
                else Send(stream, 404, "text/plain", "not found");
            }
        }
        catch { /* a closed tab mid-request */ }
    }

    // Open pages: the ledger builds the page's data only while one is connected (the page keeps a stream open), and
    // the heavier tables only for what a page shows (its venue, tab and expanded rows, sent by the page).
    static int streams;
    internal static bool Viewing => Volatile.Read(ref streams) > 0;

    internal sealed class View
    {
        public string Tab = "Dashboard", Venue;
        public HashSet<string> Open = new();
    }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, View> views = new();
    internal static List<View> Views() => views.Values.ToList();

    static string QueryValue(string query, string name)
    {
        foreach (var part in (query ?? "").Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == name) return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

    // The page says what it shows: { client, tab, venue, open: [row keys] }.
    static string SetView(string body)
    {
        try
        {
            var j = JsonDocument.Parse(body).RootElement;
            string client = j.TryGetProperty("client", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(client)) return "{\"error\":\"no client\"}";
            var v = new View
            {
                Tab = j.TryGetProperty("tab", out var t) ? t.GetString() : "Dashboard",
                Venue = j.TryGetProperty("venue", out var ve) ? ve.GetString() : null,
            };
            if (j.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.Array)
                foreach (var k in o.EnumerateArray()) if (k.ValueKind == JsonValueKind.String) v.Open.Add(k.GetString());
            views[client] = v;
            Ledger.WakeUp();
            return "{\"ok\":true}";
        }
        catch { return "{\"error\":\"bad json\"}"; }
    }

    // Each snapshot as it changes; a comment every 15 s keeps the connection open.
    static void Stream(NetworkStream stream, string client)
    {
        var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n");
        stream.Write(head);
        Interlocked.Increment(ref streams);
        if (!string.IsNullOrEmpty(client)) views.TryAdd(client, new View());
        Ledger.WakeUp();   // build fresh data now, not at the idle pace
        try { StreamLoop(stream); }
        finally
        {
            Interlocked.Decrement(ref streams);
            if (!string.IsNullOrEmpty(client)) views.TryRemove(client, out _);
        }
    }

    static void StreamLoop(NetworkStream stream)
    {
        int sent = -1;
        var quiet = DateTime.UtcNow;
        while (true)
        {
            if (Ledger.Version != sent)
            {
                sent = Ledger.Version;
                stream.Write(Encoding.UTF8.GetBytes($"data: {Ledger.Snapshot}\n\n"));
                quiet = DateTime.UtcNow;
            }
            else if ((DateTime.UtcNow - quiet).TotalSeconds > 15)
            {
                stream.Write(Encoding.ASCII.GetBytes(":\n\n"));
                quiet = DateTime.UtcNow;
            }
            Thread.Sleep(300);
        }
    }

    // Changes run on the main thread; the reply waits for them (up to 3 s).
    static string Change(string path, string body)
    {
        JsonElement j;
        try { j = JsonDocument.Parse(body).RootElement; } catch { return "{\"error\":\"bad json\"}"; }
        string Str(string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

        var done = new TaskCompletionSource<string>();
        switch (path)
        {
            case "/api/cost":
                Ledger.Post(() => { Ledger.SetCostOverride(Str("ingredient"), Int("unit")); done.SetResult("{\"ok\":true}"); });
                break;
            case "/api/batch":
                Ledger.Post(() =>
                {
                    Ledger.SetBatchCost(Str("venue"), Str("ingredient"), Int("id") ?? 0, Int("unit") ?? -1);
                    done.SetResult("{\"ok\":true}");
                });
                break;
            case "/api/linecost":
                if (!TryParseLot(Str("text"), out var count, out var unit)) return "{\"error\":\"write it as 10@3.30, 10@330 or @3.30\"}";
                Ledger.Post(() =>
                {
                    int n = Ledger.SetLineCost(Str("venue"), Str("ingredient"), count, unit);
                    done.SetResult(JsonSerializer.Serialize(new { ok = true, repriced = n, asked = count }));
                });
                break;
            default:
                return "{\"error\":\"unknown\"}";
        }
        return done.Task.Wait(3000) ? done.Task.Result : "{\"error\":\"the game didn't answer (paused or loading?)\"}";
    }

    // "10@330" (hundredths), "10@3.30" (a dot means currency), "@3.30" (all unconfirmed).
    internal static bool TryParseLot(string text, out int? count, out int unit)
    {
        count = null;
        unit = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Replace(" ", "").Split('@');
        if (parts.Length != 2) return false;
        if (parts[0].Length > 0)
        {
            if (!int.TryParse(parts[0], out var c) || c <= 0) return false;
            count = c;
        }
        string p = parts[1].Replace(",", ".");
        if (p.Contains('.'))
        {
            if (!decimal.TryParse(p, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var money) || money < 0) return false;
            unit = (int)Math.Round(money * 100m);
        }
        else if (!int.TryParse(p, out unit) || unit < 0) return false;
        return true;
    }

    static (string method, string path, System.Collections.Generic.Dictionary<string, string> headers, string body) Read(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        // Head, up to the blank line.
        while (buffer.Length < 16384)
        {
            if (stream.Read(one, 0, 1) <= 0) return (null, null, null, null);
            buffer.WriteByte(one[0]);
            var b = buffer.GetBuffer();
            long n = buffer.Length;
            if (n >= 4 && b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return (null, null, null, null);
        var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines[1..])
        {
            int i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim().ToLowerInvariant()] = line[(i + 1)..].Trim();
        }
        string body = "";
        if (headers.TryGetValue("content-length", out var len) && int.TryParse(len, out var length) && length > 0 && length < 65536)
        {
            var data = new byte[length];
            int read = 0;
            while (read < length)
            {
                int r = stream.Read(data, read, length - read);
                if (r <= 0) break;
                read += r;
            }
            body = Encoding.UTF8.GetString(data, 0, read);
        }
        string path = first[1];
        int q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        return (first[0], path, headers, body);
    }

    static void Send(NetworkStream stream, int status, string type, string content)
    {
        var data = Encoding.UTF8.GetBytes(content);
        string reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {data.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        stream.Write(head);
        stream.Write(data);
    }
}
