using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using NivalisModKit;
using UnityEngine;

namespace NivalisLedger;

// The in-game panel: today's numbers for a venue, what's running low and the latest sales, on a key (F8). Its look is a
// kit layout made in the UI Studio from ui/panel.html; the DLL ships ui/panel.layout.json, and a file at
// BepInEx\config\NivalisLedger\panel.layout.json replaces it (watched, so a studio export shows up live).
internal static class Panel
{
    static LayoutView view;
    static ConfigEntry<string> keyName;
    static UnityEngine.InputSystem.Key? key;
    static int filledVersion = -1;

    internal static string LayoutPath => Path.Combine(Paths.ConfigPath, "NivalisLedger", "panel.layout.json");

    internal static void Install(ConfigFile config)
    {
        keyName = config.Bind("Panel", "ToggleKey", "F8",
            "Key that shows and hides the in-game Ledger panel (a Unity Input System key name, e.g. F8, L). Empty: none.");
        keyName.SettingChanged += (_, _) => ReadKey();
        ReadKey();

        string json;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NivalisLedger.panel.layout.json"))
        using (var r = new StreamReader(s))
            json = r.ReadToEnd();
        view = LayoutView.Load(Plugin.Guid, json, LayoutPath);
        if (view == null) return;
        view.Reloaded += () => filledVersion = -1;
    }

    static void ReadKey()
    {
        string v = keyName.Value?.Trim();
        key = null;
        if (string.IsNullOrEmpty(v)) return;
        if (Enum.TryParse(v, true, out UnityEngine.InputSystem.Key k)) key = k;
        else Plugin.L.LogWarning($"Ledger: unknown [Panel] ToggleKey '{v}'");
    }

    internal static void Tick()
    {
        if (view == null) return;
        if (!GameEvents.IsInGame) { if (view.Visible) view.Visible = false; return; }
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (key is { } k && kb != null && kb[k].wasPressedThisFrame) { view.Visible = !view.Visible; filledVersion = -1; }
        if (!view.Visible || filledVersion == Ledger.Version) return;
        filledVersion = Ledger.Version;
        try { Fill(); }
        catch (Exception e) { Plugin.L.LogWarning($"Ledger panel: {e.Message}"); }
    }

    static readonly CultureInfo Num = CultureInfo.InvariantCulture;
    static string Money(long hundredths) => (hundredths / 100.0).ToString("N2", Num);
    static readonly Color Good = new Color32(0x4f, 0xd1, 0x8b, 0xff), Bad = new Color32(0xff, 0x6b, 0x6b, 0xff),
                          Warn = new Color32(0xf0, 0xa0, 0x4b, 0xff), Plain = new Color32(0xe6, 0xe8, 0xee, 0xff);

    // From the same snapshot the web page gets: the first venue.
    static void Fill()
    {
        using var doc = JsonDocument.Parse(Ledger.Snapshot);
        var s = doc.RootElement;
        view.SetText("clock", Str(s, "time"));
        if (!s.TryGetProperty("venues", out var venues) || venues.GetArrayLength() == 0)
        {
            view.SetText("venue", "No venue of yours yet");
            foreach (var n in new[] { "revenue", "cogs", "profit", "stock" }) view.SetText(n, "–");
            view.Repeat("low", 0, null);
            view.Repeat("sale", 0, null);
            view.SetShown("noSales", false);
            return;
        }
        var v = venues[0];
        string district = Str(v, "district");
        view.SetText("venue", string.IsNullOrEmpty(district) ? Str(v, "name") : $"{Str(v, "name")} · {district}");
        var today = v.GetProperty("today");
        view.SetText("revenue", Money(Long(today, "revenue")));
        view.SetText("cogs", Money(Long(today, "costOfSales")));
        long profit = Long(v, "profitToday");
        view.SetText("profit", Money(profit));
        view.SetColor("profit", profit < 0 ? Bad : profit > 0 ? Good : Plain);
        view.SetText("stock", Money(Long(v, "stockOnHand")));

        var low = v.GetProperty("lowStock").EnumerateArray().ToList();
        long most = Math.Max(1, low.Count == 0 ? 1 : low.Max(x => Long(x, "stock")));
        view.Repeat("low", low.Count, row =>
        {
            var x = low[row.Index];
            long n = Long(x, "stock");
            row.SetText("name", Str(x, "name"));
            row.SetText("count", n.ToString(Num));
            row.SetFill("bar", Math.Max(0.03f, n / (float)most));
            row.SetColor("bar", n <= 5 ? Bad : n <= 15 ? Warn : Good);
        });

        var sales = v.GetProperty("sales").EnumerateArray().ToList();
        view.SetShown("noSales", sales.Count == 0);   // the feed starts empty after loading
        view.Repeat("sale", sales.Count, row =>
        {
            var x = sales[row.Index];
            string time = Str(x, "time");
            row.SetText("dish", Str(x, "dish"));
            row.SetText("when", time.Contains(' ') ? time[(time.LastIndexOf(' ') + 1)..] : time);
            row.SetText("price", Money(Long(x, "price")));
        });
    }

    static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    static long Long(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
