using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;
using NivalisModKit;
using UnityEngine;

namespace NivalisLedger;

// The bookkeeping, per player venue. Money is in hundredths, as the game keeps it.
//
// Today's totals come from the venue's receipts (what the end-of-day screen reads), so loading mid-day loses nothing:
// sales by dish, ingredient spend, wages. Events add what receipts lack: the live sales feed, purchases by ingredient,
// who was paid.
//
// Ingredient cost is a moving average per venue and ingredient: a quantity and a total value, the average being value
// over quantity. Manager purchases are held as "in transit" lots at what was paid and join when the delivery reaches
// storage. The player's inventory is a location too: their vendor purchases join it at what they paid. Moving stock
// between the inventory and a venue moves its value at the source's average (a venue rising while the inventory falls by
// the same amount is a transfer). Stock that appears from nowhere (rewards, gifts, stock from before the ledger) joins
// at the market price, marked estimated, zero if just harvested; it can be repriced from the web page ("10@330"). Using
// stock (cooking, eating, selling) lowers the quantity and keeps the average.
internal static class Ledger
{
    // ---------- kept with the save ----------

    internal sealed class Lot { public int Count { get; set; } public int Unit { get; set; } }

    internal sealed class Batch
    {
        public int Id { get; set; }
        public int Count { get; set; }
        public int Unit { get; set; }
        public string Source { get; set; }      // delivery, bought, grown, estimated, opening
        public string Time { get; set; }
        public bool Edited { get; set; }
    }

    internal sealed class StockLine
    {
        public string Name { get; set; }
        public int Qty { get; set; }
        public long Value { get; set; }
        public int LastAverage { get; set; }
        public List<Lot> InTransit { get; set; } = new();
        public List<Batch> Batches { get; set; } = new();
        public int Average => Qty > 0 ? (int)(Value / Qty) : LastAverage;
    }

    internal sealed class Purchases
    {
        public string Name { get; set; }
        public int QtyToday { get; set; }
        public int SpendToday { get; set; }
        public int LastUnit { get; set; }
        public int DeliveredToday { get; set; }
    }

    internal sealed class WageLine
    {
        public string Name { get; set; }
        public int PaidHours { get; set; }
        public int Paid { get; set; }
        public int MissedHours { get; set; }
        public string LastPaid { get; set; }
    }

    internal sealed class DayRecord
    {
        public int Day { get; set; }
        public int Revenue { get; set; }
        public int Ingredients { get; set; }
        public int Wages { get; set; }
        public int Rent { get; set; }
        public int ByYou { get; set; }        // stock the player carried into this venue (tracked days only)
        public int YourSpending { get; set; } // all the player's vendor purchases (not in profit: includes furniture etc.)
        public int Wasted { get; set; }       // cost of stock that spoiled (already paid for; not counted again in profit)
        public int Sales { get; set; }
        public int CostOfSales { get; set; }  // plate cost of each dish when it sold (stock becomes a cost when used)
        public bool Tracked { get; set; }     // the ledger was running that day (cost of sales known)
        // Profit counts stock when it's sold or spoils, not when it's bought: buying turns cash into stock.
        public int Profit => Revenue - CostOfSales - Wasted - Wages - Rent;
    }

    internal sealed class Waste { public string Name { get; set; } public int Qty { get; set; } public long Value { get; set; } }

    internal sealed class DishTotals { public string Name { get; set; } public int Sold { get; set; } public int Revenue { get; set; } }

    internal sealed class Book
    {
        public string Name { get; set; }
        public Dictionary<string, StockLine> Stock { get; set; } = new();
        public Dictionary<string, Purchases> Bought { get; set; } = new();
        public Dictionary<string, WageLine> WagesToday { get; set; } = new();
        public Dictionary<string, DishTotals> DishesAll { get; set; } = new();
        public List<DayRecord> History { get; set; } = new();
        public bool Opened { get; set; }       // stock seen at least once (opening stock valued)
        public int TrackingSince { get; set; } // the game day the ledger started on this venue
        public int ByYouToday { get; set; }    // value of the player's own purchases put into this venue's storage today
        public Dictionary<string, Waste> WastedToday { get; set; } = new();
        public int CostOfSalesToday { get; set; }                         // plate costs of sales seen live today
        public Dictionary<string, int> SoldSeenToday { get; set; } = new(); // dish -> sales seen live today
    }

    sealed class Sale { public string Dish; public int Price; public string Time; }

    const string Owner = "bgasm.nivalis.ledger";
    const string RottenFood = "RottenFood";   // what spoiled food becomes (no value; spoilage is recorded as waste)
    static Dictionary<string, Book> books = new();               // venue key -> book
    static Dictionary<string, int> costOverrides = new();        // ingredient -> unit cost used for plates
    static Dictionary<string, StockLine> pocket = new();          // the player's inventory, valued like a venue's storage
    static bool pocketOpened;
    static Dictionary<string, Waste> pocketWasted = new();
    static Dictionary<string, int> grown = new();                // ingredient -> harvested, not yet seen in the inventory
    static int nextBatchId = 1;

    static readonly Dictionary<string, List<Sale>> feed = new();
    static readonly Dictionary<string, Dictionary<string, (string[] dishes, int price)>> openOrders = new();
    static readonly Dictionary<string, List<(string id, string[] dishes, int price, float at)>> doneOrders = new();
    static readonly Dictionary<string, DayRecord> lastToday = new();
    static readonly Dictionary<string, Dictionary<string, int>> plateCosts = new();   // venue -> dish -> plate cost now
    // Today's stock levels per venue and ingredient, every half hour of game time (for the "running low" sparklines),
    // and what was used and received today (from the half-hourly changes).
    static readonly Dictionary<string, Dictionary<string, List<int>>> stockSeries = new();
    static readonly Dictionary<string, Dictionary<string, (int used, int received, int last)>> stockFlow = new();
    static readonly Dictionary<string, int> lastSlot = new();
    static int sampleDay = -1;
    static readonly ConcurrentQueue<Action> fromServer = new();
    static readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static float snapshotAt;
    static List<ReceiptInfo> playerReceipts = new();

    // The latest snapshot, for the server (written on the main thread, read on server threads).
    internal static volatile string Snapshot = "{\"venues\":[]}";
    internal static volatile int Version;

    internal static void Install()
    {
        SaveData.Loaded += Load;
        SaveData.Saving += Save;

        GameEvents.SaleMade += a => Record(a.Area, b =>
        {
            string dish = Key(a.Meal);
            if (plateCosts.TryGetValue(KeyOf(a.Area), out var costs) && costs.TryGetValue(dish, out var cost)) b.CostOfSalesToday += cost;
            b.SoldSeenToday[dish] = (b.SoldSeenToday.TryGetValue(dish, out var seen) ? seen : 0) + 1;
            if (!b.DishesAll.TryGetValue(dish, out var t)) b.DishesAll[dish] = t = new DishTotals { Name = NameOf(a.Meal) };
            t.Sold++;
            t.Revenue += a.Price;
            var list = Feed(KeyOf(a.Area));
            list.Add(new Sale { Dish = NameOf(a.Meal), Price = a.Price, Time = Clock() });
            if (list.Count > 40) list.RemoveAt(0);
        });

        // Manager purchases: priced now, join the stock value when delivered.
        GameEvents.IngredientsPurchased += a => Record(a.Area, b =>
        {
            if (a.Count <= 0) return;
            var p = Bought(b, a.Item);
            p.QtyToday += a.Count;
            p.SpendToday += a.TotalPrice;
            p.LastUnit = a.TotalPrice / a.Count;
            Line(b, a.Item).InTransit.Add(new Lot { Count = a.Count, Unit = a.TotalPrice / a.Count });
        });
        GameEvents.DeliveryCompleted += a => Record(a.Area, b =>
        {
            foreach (var kv in a.Items)
            {
                Bought(b, kv.Key).DeliveredToday += kv.Value;
                var line = Line(b, kv.Key);
                int left = kv.Value;
                while (left > 0 && line.InTransit.Count > 0)
                {
                    var lot = line.InTransit[0];
                    int n = Math.Min(left, lot.Count);
                    AddBatch(line, n, lot.Unit, "delivery");
                    lot.Count -= n;
                    left -= n;
                    if (lot.Count == 0) line.InTransit.RemoveAt(0);
                }
                if (left > 0) AddBatch(line, left, Bought(b, kv.Key).LastUnit, "delivery");
            }
        });

        // Bought by the player: joins their inventory at what they paid (the inventory check then finds it expected).
        GameEvents.PlayerBought += a =>
        {
            if (a.Item == null || a.Count <= 0) return;
            AddBatch(PocketLine(a.Item), a.Count, a.TotalPrice / a.Count, "bought");
            Version++;
        };
        // Harvested by the player: straight into their inventory, at no cost (seeds, rent and supplies aren't counted).
        // Anywhere else, remembered so it's free when it reaches the inventory.
        GameEvents.ProduceHarvested += a =>
        {
            if (a.Item == null || a.Count <= 0) return;
            if (a.IntoPlayerInventory) AddBatch(PocketLine(a.Item), a.Count, 0, "grown");
            else
            {
                string k = Key(a.Item);
                grown[k] = (grown.TryGetValue(k, out var n) ? n : 0) + a.Count;
            }
            Version++;
        };

        // Spoiled: leaves its location at that location's average, and is recorded as waste.
        GameEvents.FoodSpoiled += a =>
        {
            try
            {
                if (a.Container == null) return;
                IntPtr c = a.Container.Pointer;
                Dictionary<string, StockLine> lines = null;
                Dictionary<string, Waste> wasted = null;
                bool pocketHit = false;
                try
                {
                    pocketHit = Nivalis.Singleton<Nivalis.PlayerManager>.InstanceExist(out var pm) &&
                                pm.LocalPlayer?.Inventory?.Items?.Pointer == c;
                }
                catch { }
                if (pocketHit) { lines = pocket; wasted = pocketWasted; }
                else
                    foreach (var area in Venues.PlayerOwned)
                    {
                        var inv = area.JointInventory;
                        if (inv == null || (inv.NormalInventory?.Pointer != c && inv.RefridgeratedInventory?.Pointer != c)) continue;
                        var book = BookOf(area);
                        lines = book.Stock;
                        wasted = book.WastedToday;
                        break;
                    }
                if (lines == null) return;   // not storage the ledger follows
                foreach (var kv in a.Items)
                {
                    string k = Key(kv.Key);
                    if (!lines.TryGetValue(k, out var line)) continue;
                    int n = Math.Min(kv.Value, line.Qty);
                    if (n <= 0) continue;
                    long value = (long)line.Average * n;
                    Use(line, n);
                    if (!wasted.TryGetValue(k, out var w)) wasted[k] = w = new Waste { Name = NameOf(kv.Key) };
                    w.Qty += n;
                    w.Value += value;
                }
                Version++;
            }
            catch (Exception e) { Plugin.L.LogWarning($"Ledger: spoilage: {e.Message}"); }
        };

        GameEvents.StaffPaid += a => Record(a.Area, b =>
        {
            string name = PersonName(a.Person);
            if (!b.WagesToday.TryGetValue(name, out var w)) b.WagesToday[name] = w = new WageLine { Name = name };
            if (a.Paid) { w.PaidHours++; w.Paid += a.Wage; w.LastPaid = Clock(); }
            else w.MissedHours++;
        });

        GameEvents.DayEnded += a =>
        {
            foreach (var (key, b) in books)
            {
                if (lastToday.TryGetValue(key, out var today))
                {
                    b.History.Add(new DayRecord
                    {
                        Day = a.Day, Revenue = today.Revenue, Ingredients = today.Ingredients, Wages = today.Wages,
                        Rent = today.Rent, ByYou = today.ByYou, YourSpending = today.YourSpending, Sales = today.Sales,
                        Wasted = today.Wasted, CostOfSales = today.CostOfSales, Tracked = true,
                    });
                    if (b.History.Count > 90) b.History.RemoveAt(0);
                }
                b.WagesToday.Clear();
                b.ByYouToday = 0;
                b.WastedToday.Clear();
                b.CostOfSalesToday = 0;
                b.SoldSeenToday.Clear();
                stockSeries.Clear();
                stockFlow.Clear();
                foreach (var p in b.Bought.Values) { p.QtyToday = 0; p.SpendToday = 0; p.DeliveredToday = 0; }
            }
            pocketWasted.Clear();
            Version++;
        };
    }

    // ---------- per frame (main thread) ----------

    internal static void Tick()
    {
        while (fromServer.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { Plugin.L.LogWarning($"Ledger: {e.Message}"); }
        }
        if (!GameEvents.IsInGame || Time.unscaledTime < snapshotAt) return;
        // Bookkeeping (stock moves, plate costs, today's totals) keeps going; the page's data is built only while a page
        // is open. Slower when nobody's watching, slower again while the game is paused (little changes then).
        bool viewing = Server.Viewing, paused = Time.timeScale <= 0f;
        snapshotAt = Time.unscaledTime + (viewing ? (paused ? 2f : 1f) : (paused ? 5f : 3f));
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var built = BuildSnapshot(viewing);
            if (built != null) { Snapshot = built; Version++; }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            // A pass long enough to show as a hitch: say so (once a minute at most), for stutter reports.
            if (ms > 8.0 && Time.unscaledTime >= slowLoggedAt)
            {
                slowLoggedAt = Time.unscaledTime + 60f;
                Plugin.L.LogWarning($"Ledger: an update took {ms:0} ms ({(viewing ? "page open" : "page closed")}, {Venues.PlayerOwned.Count} venue(s))");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"Ledger: snapshot failed: {e}"); }
    }

    static float slowLoggedAt;

    // A page just connected: build its data on the next frame.
    internal static void WakeUp() => snapshotAt = 0f;

    // Changes from the web page run on the main thread.
    internal static void Post(Action action) => fromServer.Enqueue(action);

    internal static void SetCostOverride(string ingredient, int? unitCost)
    {
        if (string.IsNullOrEmpty(ingredient)) return;
        if (unitCost is { } c && c >= 0) costOverrides[ingredient] = c;
        else costOverrides.Remove(ingredient);
        snapshotAt = 0f;
    }

    // A batch's price corrected: the stock value moves by the difference over the batch (if any stock is left).
    internal static void SetBatchCost(string venue, string ingredient, int batchId, int unit)
    {
        var line = LineFor(venue, ingredient);
        if (unit < 0 || line == null) return;
        var batch = line.Batches.FirstOrDefault(x => x.Id == batchId);
        if (batch == null) return;
        Reprice(line, batch, unit);
        snapshotAt = 0f;
    }

    // "10@330" on an ingredient line: the most recent unconfirmed batches (estimated or opening stock, not yet
    // repriced), up to 10 units, are repriced at 330 each; count null = all of them. A batch only partly covered is
    // split. Returns how many units were repriced.
    internal static int SetLineCost(string venue, string ingredient, int? count, int unit)
    {
        var line = LineFor(venue, ingredient);
        if (unit < 0 || line == null) return 0;
        int left = count ?? int.MaxValue, done = 0;
        foreach (var batch in line.Batches.AsEnumerable().Reverse().Where(x => !x.Edited && x.Source is "estimated" or "opening").ToList())
        {
            if (left <= 0) break;
            if (batch.Count > left)
            {
                // Split: the repriced part becomes its own batch.
                batch.Count -= left;
                var part = new Batch { Id = nextBatchId++, Count = left, Unit = batch.Unit, Source = batch.Source, Time = batch.Time };
                line.Batches.Insert(line.Batches.IndexOf(batch) + 1, part);
                Reprice(line, part, unit);
                done += left;
                left = 0;
            }
            else
            {
                Reprice(line, batch, unit);
                done += batch.Count;
                left -= batch.Count;
            }
        }
        snapshotAt = 0f;
        return done;
    }

    static void Reprice(StockLine line, Batch batch, int unit)
    {
        if (line.Qty > 0) line.Value = Math.Max(0, line.Value + (long)Math.Min(batch.Count, line.Qty) * (unit - batch.Unit));
        batch.Unit = unit;
        batch.Edited = true;
        line.LastAverage = line.Average;
    }

    // ---------- stock value ----------

    static void AddBatch(StockLine line, int count, int unit, string source)
    {
        if (count <= 0) return;
        line.Qty += count;
        line.Value += (long)count * unit;
        line.LastAverage = line.Average;
        line.Batches.Add(new Batch { Id = nextBatchId++, Count = count, Unit = unit, Source = source, Time = Clock() });
        if (line.Batches.Count > 12) line.Batches.RemoveAt(0);
    }

    // Storage and inventory against what the ledger expects, all at once, so transfers are recognised: the inventory
    // falling while a venue rises by the same amount is stock carried in (at the inventory's average), and the reverse is
    // stock taken out (at the venue's average). What's left over: increases from nowhere (market price, estimated; zero
    // if harvested) and use (at the average).
    static void Reconcile(List<(Book book, string name, Dictionary<string, (ItemType item, int count)> stock)> venues,
                          Dictionary<string, (ItemType item, int count)> carried)
    {
        var keys = new HashSet<string>(carried.Keys);
        keys.UnionWith(pocket.Keys);
        foreach (var (b, _, st) in venues) { keys.UnionWith(st.Keys); keys.UnionWith(b.Stock.Keys); }

        keys.Remove(RottenFood);
        foreach (var k in keys)
        {
            ItemType item = carried.TryGetValue(k, out var c) ? c.item : null;
            foreach (var (_, _, st) in venues) if (item == null && st.TryGetValue(k, out var x)) item = x.item;

            // The inventory.
            int pActual = carried.TryGetValue(k, out var pc) ? pc.count : 0;
            pocket.TryGetValue(k, out var pLine);
            if (pLine == null && pActual > 0) pocket[k] = pLine = new StockLine { Name = NameOf(item) ?? k };
            int pd = pLine == null ? 0 : pActual - pLine.Qty;
            if (pLine != null && !pocketOpened)
            {
                if (pd > 0) AddBatch(pLine, pd, Market(item), "opening"); else if (pd < 0) Use(pLine, -pd);
                pd = 0;
            }

            // The venues.
            var moves = new List<(Book book, string name, StockLine line, int d)>();
            foreach (var (b, name, st) in venues)
            {
                int actual = st.TryGetValue(k, out var x) ? x.count : 0;
                b.Stock.TryGetValue(k, out var line);
                if (line == null)
                {
                    if (actual == 0) continue;
                    b.Stock[k] = line = new StockLine { Name = NameOf(item) ?? k };
                }
                int d = actual - line.Qty;
                if (!b.Opened)
                {
                    if (d > 0) AddBatch(line, d, Market(item), "opening"); else if (d < 0) Use(line, -d);
                    continue;
                }
                if (d != 0) moves.Add((b, name, line, d));
            }

            // Carried in: inventory -> venue, at the inventory's average.
            for (int i = 0; i < moves.Count && pd < 0; i++)
            {
                if (moves[i].d <= 0) continue;
                int n = Math.Min(-pd, moves[i].d), unit = pLine.Average;
                Use(pLine, n);
                AddBatch(moves[i].line, n, unit, "from you");
                moves[i].book.ByYouToday += n * unit;
                pd += n;
                moves[i] = (moves[i].book, moves[i].name, moves[i].line, moves[i].d - n);
            }
            // Taken out: venue -> inventory, at the venue's average.
            for (int i = 0; i < moves.Count && pd > 0; i++)
            {
                if (moves[i].d >= 0) continue;
                int n = Math.Min(pd, -moves[i].d), unit = moves[i].line.Average;
                Use(moves[i].line, n);
                AddBatch(pLine, n, unit, "from " + moves[i].name);
                moves[i].book.ByYouToday -= n * unit;
                pd -= n;
                moves[i] = (moves[i].book, moves[i].name, moves[i].line, moves[i].d + n);
            }

            // Left over.
            foreach (var (_, _, line, d) in moves)
            {
                if (d > 0) AddBatch(line, d, Market(item), "estimated");
                else if (d < 0) Use(line, -d);
            }
            if (pLine != null)
            {
                if (pd > 0 && grown.TryGetValue(k, out var g) && g > 0) { AddBatch(pLine, pd, 0, "grown"); grown[k] = Math.Max(0, g - pd); }
                else if (pd > 0) AddBatch(pLine, pd, Market(item), "estimated");
                else if (pd < 0) Use(pLine, -pd);
            }
        }
        foreach (var (b, _, _) in venues) b.Opened = true;
        pocketOpened = true;
    }

    // Stock used (or moved out): the quantity drops, the average stays.
    static void Use(StockLine line, int n)
    {
        int avg = line.Average;
        line.Value = Math.Max(0, line.Value - (long)avg * n);
        line.Qty -= n;
        if (line.Qty <= 0) { line.Qty = 0; line.Value = 0; }
        line.LastAverage = avg;
    }

    static StockLine PocketLine(ItemType item)
    {
        string k = Key(item);
        if (!pocket.TryGetValue(k, out var line)) pocket[k] = line = new StockLine { Name = NameOf(item) };
        return line;
    }

    // The line the web page means: a venue's, or the inventory's ("pocket").
    static StockLine LineFor(string venue, string ingredient)
    {
        if (venue == "pocket") return pocket.TryGetValue(ingredient, out var p) ? p : null;
        return books.TryGetValue(venue, out var b) && b.Stock.TryGetValue(ingredient, out var line) ? line : null;
    }

    // Unit cost used for plates: the player's override, else the stock average, else the market price.
    static (int unit, string source) UnitCost(Book book, ItemType item)
    {
        string k = Key(item);
        if (costOverrides.TryGetValue(k, out var o)) return (o, "override");
        if (book.Stock.TryGetValue(k, out var line) && (line.Qty > 0 || line.LastAverage > 0))
            return (line.Average, line.Batches.Any(x => x.Source is "estimated" or "opening" && !x.Edited) ? "average (part estimated)" : "average");
        return (Market(item), "market");
    }

    // The item's base price (what vendors start from), from the per-item cache.
    static int Market(ItemType item) => Names(item).market;

    // ---------- the Ingredients and inventory tables ----------

    // Rows change only when stock moves, something is bought, delivered or spoils, or a price is set: each table's rows
    // are kept (as JSON) with a fingerprint of what they're built from, and rebuilt only when that changes.
    sealed class Rows { public int Sig; public JsonElement Json; }
    static readonly Dictionary<string, Rows> rowsCache = new();

    static int Mix(int h, int v) => unchecked(h * 31 + v);

    static int LineSig(int h, StockLine line)
    {
        if (line == null) return Mix(h, -1);
        h = Mix(Mix(Mix(h, line.Qty), (int)line.Value), line.LastAverage);
        foreach (var b in line.Batches) h = Mix(Mix(Mix(Mix(h, b.Id), b.Count), b.Unit), b.Edited ? 1 : 0);
        foreach (var t in line.InTransit) h = Mix(Mix(h, t.Count), t.Unit);
        return h;
    }

    static JsonElement Cached(string cacheKey, int sig, Func<object> build)
    {
        if (rowsCache.TryGetValue(cacheKey, out var r) && r.Sig == sig) return r.Json;
        rowsCache[cacheKey] = r = new Rows { Sig = sig, Json = JsonSerializer.SerializeToElement(build(), json) };
        return r.Json;
    }

    static object Batches(StockLine line) =>
        line?.Batches.AsEnumerable().Reverse().Select(x => new { x.Id, x.Count, x.Unit, x.Source, x.Time, x.Edited }).ToList();

    static JsonElement IngredientRows(string venue, Book book, Dictionary<string, (ItemType item, int count)> stock,
        List<ItemType> menuItems, HashSet<string> open)
    {
        var onMenu = new HashSet<string>(menuItems.Select(Key));
        var items = new Dictionary<string, ItemType>();
        foreach (var i in menuItems) items.TryAdd(Key(i), i);
        foreach (var kv in stock) items[kv.Key] = kv.Value.item;
        var keys = new HashSet<string>(items.Keys);
        keys.UnionWith(book.Bought.Keys);
        keys.UnionWith(book.Stock.Where(kv => kv.Value.Qty > 0 || kv.Value.InTransit.Count > 0).Select(kv => kv.Key));

        // The fingerprint: everything a row shows.
        int sig = 17;
        foreach (var k in keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            book.Stock.TryGetValue(k, out var line);
            book.Bought.TryGetValue(k, out var p);
            book.WastedToday.TryGetValue(k, out var w);
            sig = Mix(sig, k.GetHashCode());
            sig = Mix(sig, stock.TryGetValue(k, out var s) ? s.count : -1);
            sig = LineSig(sig, line);
            sig = Mix(Mix(Mix(sig, p?.QtyToday ?? 0), p?.SpendToday ?? 0), p?.DeliveredToday ?? 0);
            sig = Mix(Mix(sig, w?.Qty ?? 0), (int)(w?.Value ?? 0));
            sig = Mix(sig, costOverrides.TryGetValue(k, out var o) ? o : -1);
            sig = Mix(Mix(sig, onMenu.Contains(k) ? 1 : 0), open.Contains(k) ? 1 : 0);
        }

        return Cached("venue:" + venue, sig, () => keys.Select(k =>
        {
            book.Stock.TryGetValue(k, out var line);
            book.Bought.TryGetValue(k, out var p);
            book.WastedToday.TryGetValue(k, out var w);
            items.TryGetValue(k, out var item);
            var (unit, source) = item != null ? UnitCost(book, item) : (line?.Average ?? 0, "average");
            return new
            {
                key = k, name = line?.Name ?? p?.Name ?? NameOf(item) ?? k,
                stock = stock.TryGetValue(k, out var s) ? s.count : 0,
                inTransit = line?.InTransit.Sum(l => l.Count) ?? 0,
                onMenu = onMenu.Contains(k),
                boughtToday = p?.QtyToday ?? 0, spendToday = p?.SpendToday ?? 0, deliveredToday = p?.DeliveredToday ?? 0,
                wastedToday = w?.Qty ?? 0, wastedValue = w?.Value ?? 0,
                average = line?.Average, stockValue = line?.Value ?? 0, market = Market(item),
                overrideUnit = costOverrides.TryGetValue(k, out var o) ? o : (int?)null,
                unitCost = unit, costSource = source,
                batchCount = line?.Batches.Count ?? 0,
                batches = open.Contains(k) ? Batches(line) : null,   // only for expanded rows
            };
        }).OrderBy(i => i.name).ToList());
    }

    static JsonElement PocketRows(Dictionary<string, (ItemType item, int count)> carried, HashSet<string> open)
    {
        var lines = pocket.Where(kv => kv.Value.Qty > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        int sig = 23;
        foreach (var kv in lines)
        {
            sig = LineSig(Mix(sig, kv.Key.GetHashCode()), kv.Value);
            sig = Mix(sig, pocketWasted.TryGetValue(kv.Key, out var w) ? w.Qty : 0);
            sig = Mix(sig, open.Contains(kv.Key) ? 1 : 0);
        }
        return Cached("pocket", sig, () => lines.Select(kv => new
        {
            key = kv.Key, name = kv.Value.Name ?? kv.Key, stock = kv.Value.Qty, average = kv.Value.Average,
            stockValue = kv.Value.Value,
            wastedToday = pocketWasted.TryGetValue(kv.Key, out var pw) ? pw.Qty : 0,
            market = carried.TryGetValue(kv.Key, out var c) ? Market(c.item) : 0,
            estimated = kv.Value.Batches.Any(x => x.Source is "estimated" or "opening" && !x.Edited),
            batchCount = kv.Value.Batches.Count,
            batches = open.Contains(kv.Key) ? Batches(kv.Value) : null,
        }).OrderBy(x => x.name).ToList());
    }

    // ---------- the snapshot ----------

    // Bookkeeping every time; the page's data (returned as JSON) only when view is true, else null.
    static string BuildSnapshot(bool view)
    {
        var venues = new List<object>();
        var owned = Venues.PlayerOwned;
        // The player's own receipts count against their venue when they have just one (else they're shown on their own).
        // Only today's here; all of them only when the past days are rebuilt (History).
        var mineToday = owned.Count == 1 ? Economy.PlayerReceipts(GameTime.Day) : new List<ReceiptInfo>();
        var views = view ? Server.Views() : new List<Server.View>();
        var stocks = owned.Select(a => (book: BookOf(a), name: Venues.DisplayNameOf(a) ?? "venue", stock: ByKey(Venues.StockOf(a)))).ToList();
        var carried = ByKey(Economy.PlayerStock());
        Reconcile(stocks, carried);
        for (int vi = 0; vi < owned.Count; vi++)
        {
            var area = owned[vi];
            string key = KeyOf(area);
            var book = stocks[vi].book;
            var stock = stocks[vi].stock;

            SampleStock(key, stock);

            // Today, from the receipts (the venue's, and the player's own when they have one venue).
            var receipts = Venues.ReceiptsOf(area, GameTime.Day);
            var today = Day(GameTime.Day, receipts, mineToday, key);
            today.ByYou = book.ByYouToday;
            today.Wasted = (int)book.WastedToday.Values.Sum(w => w.Value);
            lastToday[key] = today;
            var soldToday = receipts.Where(r => r.Type == "Restaurant" && r.Dish != null)
                .GroupBy(r => Key(r.Dish)).ToDictionary(g => g.Key, g => (count: g.Sum(r => r.Count), revenue: g.Sum(r => r.Amount)));
            var soldNames = receipts.Where(r => r.Type == "Restaurant" && r.Dish != null)
                .GroupBy(r => Key(r.Dish)).ToDictionary(g => g.Key, g => NameOf(g.First().Dish) ?? g.Key);

            var menuEntries = Venues.MenuOf(area);
            var costsNow = new Dictionary<string, int>();
            var menu = menuEntries.Select(m =>
            {
                int cost = 0;
                bool estimated = false;
                foreach (var (item, amount) in m.Ingredients)
                {
                    var (unit, source) = UnitCost(book, item);
                    cost += unit * amount;
                    if (source != "average" && source != "override") estimated = true;
                }
                string dish = Key(m.Dish);
                soldToday.TryGetValue(dish, out var t);
                book.DishesAll.TryGetValue(dish, out var all);
                return new
                {
                    dish = NameOf(m.Dish), price = m.Price, cost, profit = m.Price - cost,
                    margin = m.Price > 0 ? Math.Round((m.Price - cost) * 100.0 / m.Price, 1) : 0,
                    estimated, ingredients = m.Ingredients.Select(i => NameOf(i.Item)).ToArray(),
                    soldToday = t.count, revenueToday = t.revenue, profitToday = t.count * (m.Price - cost),
                    costKey = Remember(costsNow, dish, cost),
                    soldAll = all?.Sold ?? 0,
                };
            }).ToList();
            plateCosts[key] = costsNow;

            // Today's cost of sales: each sale seen live at its plate cost then; sales from before the ledger saw them
            // (a mid-day load) at today's plate costs.
            int cogs = book.CostOfSalesToday;
            foreach (var (dish, sold) in soldToday)
            {
                int seen = book.SoldSeenToday.TryGetValue(dish, out var n) ? n : 0;
                if (sold.count > seen && costsNow.TryGetValue(dish, out var c)) cogs += (sold.count - seen) * c;
            }
            today.CostOfSales = cogs;
            today.Tracked = true;
            if (!view) continue;   // the rest is only for the page
            // The heavier tables only for a page showing this venue on that tab; batches only for expanded rows.
            bool showIngredients = views.Any(x => x.Venue == key && x.Tab == "Ingredients");
            bool showOrders = views.Any(x => x.Venue == key && x.Tab == "Orders");
            var openRows = new HashSet<string>(views.Where(x => x.Venue == key).SelectMany(x => x.Open));

            // Ingredients: in stock, bought, or on the menu (built only for a page on the Ingredients tab).
            var menuItems = menuEntries.SelectMany(m => m.Ingredients.Select(i => i.Item)).Where(i => i != null).ToList();
            object ingredients = showIngredients ? IngredientRows(key, book, stock, menuItems, openRows) : null;

            // Running low: menu ingredients, lowest first, with today's levels; and how many more of each dish the stock
            // allows (the scarcest ingredient decides).
            stockSeries.TryGetValue(key, out var series);
            stockFlow.TryGetValue(key, out var flow);
            var lowStock = menuItems.GroupBy(Key).Select(g =>
            {
                string k = g.Key;
                int count = stock.TryGetValue(k, out var x) ? x.count : 0;
                var f = flow != null && flow.TryGetValue(k, out var ff) ? ff : (0, 0, 0);
                return new
                {
                    key = k, name = NameOf(g.First()), stock = count,
                    trend = series != null && series.TryGetValue(k, out var t) ? t : new List<int> { count },
                    usedToday = f.Item1, receivedToday = f.Item2,
                    wastedToday = book.WastedToday.TryGetValue(k, out var wz) ? wz.Qty : 0,
                    dishes = menuEntries.Where(m => m.Ingredients.Any(i => Key(i.Item) == k)).Select(m => NameOf(m.Dish)).ToArray(),
                };
            }).OrderBy(i => i.stock).ToList();
            var platesLeft = menuEntries.Where(m => m.Ingredients.Count > 0).Select(m =>
            {
                var limit = m.Ingredients.GroupBy(i => Key(i.Item))
                    .Select(g => (name: NameOf(g.First().Item), plates: (stock.TryGetValue(g.Key, out var x) ? x.count : 0) / Math.Max(1, g.Sum(i => i.Amount))))
                    .OrderBy(t => t.plates).First();
                return new { dish = NameOf(m.Dish), plates = limit.plates, limitedBy = limit.name };
            }).OrderBy(d => d.plates).ToList();

            // Orders: active ones, and ones that just completed (green on the page for a few seconds). Only for a page on
            // the Orders tab; tracking starts over when one comes back, so orders finished meanwhile don't all flash.
            object orders = null;
            if (showOrders)
            {
                var active = Venues.OrdersOf(area);
                bool fresh = !openOrders.ContainsKey(key);
                if (!openOrders.TryGetValue(key, out var open)) openOrders[key] = open = new();
                if (!doneOrders.TryGetValue(key, out var done)) doneOrders[key] = done = new();
                var ids = new HashSet<string>(active.Select(o => o.Id));
                foreach (var gone in open.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    if (!fresh) done.Add((gone, open[gone].dishes, open[gone].price, Time.unscaledTime));
                    open.Remove(gone);
                }
                foreach (var o in active) open[o.Id] = (o.Dishes.Select(NameOf).ToArray(), o.Price);
                done.RemoveAll(d => Time.unscaledTime - d.at > 4f);
                orders = active.Select(o => new { id = o.Id, dishes = o.Dishes.Select(NameOf).ToArray(), price = o.Price, prepared = o.Prepared, delivered = o.Delivered, done = false })
                    .Concat(done.Select(d => new { id = d.id, dishes = d.dishes, price = d.price, prepared = d.dishes.Length, delivered = d.dishes.Length, done = true }))
                    .ToList();
            }
            else { openOrders.Remove(key); doneOrders.Remove(key); }

            var staff = Venues.StaffOf(area).Select(s =>
            {
                book.WagesToday.TryGetValue(s.Name ?? "", out var w);
                return new
                {
                    name = s.Name, wage = s.Wage, shift = $"{Hour(s.ShiftStart)}-{Hour(s.ShiftEnd)}", roles = Roles(s.Roles),
                    paidHours = w?.PaidHours ?? 0, paidToday = w?.Paid ?? 0, missedHours = w?.MissedHours ?? 0,
                    lastPaid = w?.LastPaid, hoursLeft = s.HoursLeftToday, dueToday = s.HoursLeftToday * s.Wage,
                };
            }).ToList();

            var reviews = Venues.ReviewsOf(area).OrderBy(r => r.GameSeconds).ToList();   // oldest first, by time
            var recent = reviews.TakeLast(50).ToList();
            venues.Add(new
            {
                key, name = book.Name, district = World.NameOf(Venues.DistrictOf(area)),
                today, profitToday = today.Profit,
                history = History(area, book, key).days,
                stockOnHand = book.Stock.Values.Sum(l => l.Value),
                receiptSummary = WithToday(History(area, book, key), receipts, mineToday),
                mealsServed = Venues.MealsServedOf(area),
                sales = Feed(key).AsEnumerable().Reverse().Take(20).Select(x => new { dish = x.Dish, price = x.Price, time = x.Time }),
                menu, ingredients, lowStock, platesLeft,
                // Everything sold today, including dishes taken off the menu since (the menu rows only cover what's on it).
                soldTodayAll = soldToday.OrderByDescending(kv => kv.Value.count).Select(kv => new
                {
                    dish = soldNames.TryGetValue(kv.Key, out var n) ? n : kv.Key, count = kv.Value.count, revenue = kv.Value.revenue,
                    onMenu = menuEntries.Any(m => Key(m.Dish) == kv.Key),
                }),
                orders,   // null when not shown: the page keeps what it had
                staff, wagesDueToday = staff.Sum(x => x.dueToday),
                rating = recent.Count > 0 ? Math.Round(recent.Average(r => r.Score), 2) : (double?)null,
                reviews = reviews.TakeLast(30).Reverse().Select(r => new   // newest first
                {
                    id = $"{r.GameSeconds}|{r.Reviewer}|{Items.NameOf(r.Dish)}", score = r.Score, dish = NameOf(r.Dish), reviewer = r.Reviewer,
                    day = GameDayOf(r.GameSeconds), time = $"{r.GameSeconds % 86400 / 3600:00}:{r.GameSeconds % 3600 / 60:00}",
                    service = Math.Round(r.ServiceQuality, 2), cleanliness = Math.Round(r.Cleanliness, 2),
                    comfort = Math.Round(r.Comfort, 2), allFoodDelivered = r.AllFoodDelivered, reaction = r.Reaction,
                }),
                satisfaction = recent.Count == 0 ? null : new
                {
                    service = Math.Round(recent.Average(r => r.ServiceQuality), 2),
                    cleanliness = Math.Round(recent.Average(r => r.Cleanliness), 2),
                    comfort = Math.Round(recent.Average(r => r.Comfort), 2),
                    delivered = Math.Round(recent.Count(r => r.AllFoodDelivered) * 100.0 / recent.Count, 0),
                },
                popularity = Venues.PopularityOf(area),
            });
        }
        if (!view) return null;
        // The player's inventory, valued: the table only for a page showing it (the sidebar needs just count and value).
        var pocketOpen = new HashSet<string>(views.Where(x => x.Venue == "pocket").SelectMany(x => x.Open)
            .Where(k => k.StartsWith("pocket:")).Select(k => k[7..]));
        object inventory = views.Any(x => x.Venue == "pocket") ? PocketRows(carried, pocketOpen) : null;
        return JsonSerializer.Serialize(new
        {
            day = GameTime.Day, time = Clock(), money = Economy.PlayerMoney, venues, inventory,
            inventoryCount = pocket.Count(kv => kv.Value.Qty > 0),
            inventoryWastedToday = pocketWasted.Values.Sum(w => w.Value),
            inventoryValue = pocket.Values.Sum(l => l.Value),
        }, json);
    }

    // Past days don't change during a day, so they (and their receipt totals) are built once per venue per game day,
    // when a page shows them: reading every receipt the game keeps is the heaviest thing the ledger does.
    sealed class PastCache
    {
        public int Day;
        public object days;
        public Dictionary<string, Dictionary<string, (int amount, int count)>> venue, player;
    }
    static readonly Dictionary<string, PastCache> pastCache = new();

    static PastCache History(VenueAreaGhost area, Book book, string key)
    {
        if (pastCache.TryGetValue(key, out var c) && c.Day == GameTime.Day) return c;
        var all = Venues.ReceiptsOf(area).Where(r => r.Day < GameTime.Day).ToList();
        playerReceipts = Venues.PlayerOwned.Count == 1
            ? Economy.PlayerReceipts().Where(r => r.Day < GameTime.Day).ToList() : new List<ReceiptInfo>();
        c = new PastCache
        {
            Day = GameTime.Day,
            days = PastDays(book, all, key).TakeLast(30).Select(d => new { d.Day, d.Revenue, d.Ingredients, d.Wages, d.Rent, d.ByYou, d.YourSpending, d.Wasted, d.CostOfSales, d.Tracked, profit = d.Profit, d.Sales }).ToList(),
            venue = Totals(all), player = Totals(playerReceipts),
        };
        pastCache[key] = c;
        return c;
    }

    static Dictionary<string, Dictionary<string, (int amount, int count)>> Totals(List<ReceiptInfo> receipts) =>
        receipts.GroupBy(r => r.Day).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(),
            g => g.GroupBy(r => r.Type).ToDictionary(t => t.Key, t => (t.Sum(r => r.Amount), t.Sum(r => r.Count))));

    // Receipt totals by day and type, venue and player (to check the numbers against the game): past days from the
    // cache, today live.
    static object WithToday(PastCache past, List<ReceiptInfo> venueToday, List<ReceiptInfo> playerToday)
    {
        object Merge(Dictionary<string, Dictionary<string, (int amount, int count)>> pastDays, List<ReceiptInfo> today)
        {
            var all = pastDays.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(t => t.Key, t => new { amount = t.Value.amount, count = t.Value.count }));
            foreach (var kv in Totals(today))
                all[kv.Key] = kv.Value.ToDictionary(t => t.Key, t => new { amount = t.Value.amount, count = t.Value.count });
            return all;
        }
        return new { venue = Merge(past.venue, venueToday), player = Merge(past.player, playerToday) };
    }

    // Past days: rebuilt from the venue's receipts as far back as the game keeps them, plus days the ledger saved at
    // day's end (for days the receipts no longer cover).
    static IEnumerable<DayRecord> PastDays(Book book, List<ReceiptInfo> receipts, string venueKey)
    {
        var days = book.History.ToDictionary(d => d.Day);
        foreach (var day in receipts.Select(r => r.Day).Concat(playerReceipts.Select(r => r.Day)).Where(d => d < GameTime.Day).Distinct())
        {
            var record = Day(day, receipts.Where(r => r.Day == day).ToList(), playerReceipts.Where(r => r.Day == day).ToList(), venueKey);
            // Stock carried in by the player is only known for days the ledger tracked.
            if (days.TryGetValue(day, out var kept))
            {
                record.ByYou = kept.ByYou; record.Wasted = kept.Wasted;
                record.CostOfSales = kept.CostOfSales; record.Tracked = kept.Tracked;
            }
            days[day] = record;
        }
        return days.Values.OrderBy(d => d.Day);
    }

    // One day's money from receipts. Amounts are signed (in positive, out negative). Rent counts when the receipt is for
    // this venue, whoever paid it (an apartment's rent never does). The player's shop receipts don't say what was bought
    // (furniture too), so they're shown as their spending, not counted in profit.
    static DayRecord Day(int day, List<ReceiptInfo> venue, List<ReceiptInfo> player, string venueKey)
    {
        static int Out(IEnumerable<ReceiptInfo> r) => -r.Where(x => x.Amount < 0).Sum(x => x.Amount);
        bool ForVenue(ReceiptInfo r)
        {
            try { return r.Property != null && r.Property.Guid == venueKey; } catch { return false; }
        }
        return new DayRecord
        {
            Day = day,
            Revenue = venue.Where(r => r.Type == "Restaurant").Sum(r => r.Amount),
            Sales = venue.Where(r => r.Type == "Restaurant").Sum(r => r.Count),
            Ingredients = Out(venue.Where(r => r.Type == "Shop")),
            Wages = Out(venue.Where(r => r.Type == "Staff")),
            Rent = Out(venue.Where(r => r.Type == "Rent")) + Out(player.Where(r => r.Type == "Rent" && ForVenue(r))),
            YourSpending = Out(player.Where(r => r.Type == "Shop")),
        };
    }

    // Every half hour of game time: today's level per ingredient, and used / received since the last sample.
    static void SampleStock(string venue, Dictionary<string, (ItemType item, int count)> stock)
    {
        if (sampleDay != GameTime.Day) { stockSeries.Clear(); stockFlow.Clear(); lastSlot.Clear(); sampleDay = GameTime.Day; }
        int slot = GameTime.Hour * 2 + GameTime.Minute / 30;
        if (!stockSeries.TryGetValue(venue, out var series)) stockSeries[venue] = series = new();
        if (!stockFlow.TryGetValue(venue, out var flow)) stockFlow[venue] = flow = new();
        // Flow is counted every snapshot (so a delivery and the cooking around it both register).
        foreach (var k in stock.Keys.Union(flow.Keys).ToList())
        {
            int now = stock.TryGetValue(k, out var x) ? x.count : 0;
            if (!flow.TryGetValue(k, out var f)) { flow[k] = (0, 0, now); continue; }
            int d = now - f.last;
            flow[k] = (f.used + Math.Max(0, -d), f.received + Math.Max(0, d), now);
        }
        bool newSlot = !lastSlot.TryGetValue(venue, out var seen) || seen != slot;
        lastSlot[venue] = slot;
        foreach (var kv in stock)
        {
            if (!series.TryGetValue(kv.Key, out var list)) series[kv.Key] = list = new();
            if (list.Count == 0 || newSlot) list.Add(kv.Value.count);
            else list[^1] = kv.Value.count;   // within the slot: keep the latest
            if (list.Count > 48) list.RemoveAt(0);
        }
    }

    static string Remember(Dictionary<string, int> map, string dish, int cost) { map[dish] = cost; return dish; }

    // ---------- helpers ----------

    static void Record(VenueAreaGhost area, Action<Book> change)
    {
        try
        {
            if (area == null || !Venues.PlayerOwned.Any(v => v.Pointer == area.Pointer)) return;
            change(BookOf(area));
            Version++;
        }
        catch (Exception e) { Plugin.L.LogWarning($"Ledger: {e.Message}"); }
    }

    static Book BookOf(VenueAreaGhost area)
    {
        string key = KeyOf(area);
        if (!books.TryGetValue(key, out var book)) books[key] = book = new Book();
        book.Name = Venues.DisplayNameOf(area) ?? key;
        if (book.TrackingSince == 0) book.TrackingSince = GameTime.Day;
        return book;
    }

    static Dictionary<string, (ItemType item, int count)> ByKey(Dictionary<ItemType, int> counts)
    {
        var map = new Dictionary<string, (ItemType item, int count)>();
        foreach (var kv in counts)
        {
            string k = Key(kv.Key);
            map[k] = (kv.Key, (map.TryGetValue(k, out var have) ? have.count : 0) + kv.Value);
        }
        return map;
    }

    static List<Sale> Feed(string venue)
    {
        if (!feed.TryGetValue(venue, out var list)) feed[venue] = list = new();
        return list;
    }

    static StockLine Line(Book b, ItemType item)
    {
        string k = Key(item);
        if (!b.Stock.TryGetValue(k, out var line)) b.Stock[k] = line = new StockLine { Name = NameOf(item) };
        return line;
    }

    static Purchases Bought(Book b, ItemType item)
    {
        string k = Key(item);
        if (!b.Bought.TryGetValue(k, out var p)) b.Bought[k] = p = new Purchases { Name = NameOf(item) };
        return p;
    }

    // Venue key: its GUID (stable across saves and renames).
    static string KeyOf(VenueAreaGhost area)
    {
        try { return area?.Venue?.Guid ?? Venues.NameOf(area) ?? "venue"; } catch { return "venue"; }
    }

    // Item names come from the game each time (a native call and a new string), and the snapshot asks for the same
    // few hundred over and over: kept per item (items are assets, so they last the session).
    static readonly Dictionary<IntPtr, (string key, string name, int market)> names = new();

    static (string key, string name, int market) Names(ItemType item)
    {
        if (item == null) return (null, null, 0);
        if (names.TryGetValue(item.Pointer, out var n)) return n;
        string key = Items.NameOf(item), shown = null;
        int market = 0;
        try { if (!string.IsNullOrWhiteSpace(item.Name)) shown = item.Name; } catch { }
        try { market = (int)Math.Round(item.basePrice * 100f); } catch { }
        n = (key, shown ?? key, market);
        names[item.Pointer] = n;
        return n;
    }

    static string Key(ItemType item) => Names(item).key ?? "?";

    // The name players see, else the asset name.
    static string NameOf(ItemType item) => Names(item).name;

    // Roles as the Staff tab names them: the game's jobs (Cooking -> Cook...) and mod jobs as they are (Bartender).
    static readonly Dictionary<string, string> JobNames = new()
    {
        ["Cooking"] = "Cook", ["Serving"] = "Waiter", ["Cleaning"] = "Cleaner", ["Managing"] = "Manager", ["None"] = "no job",
    };

    static string Roles(string roles)
    {
        if (string.IsNullOrEmpty(roles)) return roles;
        var parts = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(", ", parts.Select(r => JobNames.TryGetValue(r, out var n) ? n : r));
    }

    // The game's day for a time in game seconds: days count from 1 and turn over at 08:00 (as on receipts). Worked out
    // here so reviews read right with any kit (before kit 0.6.2, ReviewInfo.Day counted from 0 and turned at midnight).
    static int GameDayOf(int gameSeconds) => (gameSeconds - 8 * 3600) / 86400 + 1;

    static string PersonName(Nivalis.GhostSystem.Ai.Person p)
    {
        try { return p?.DisplayedName ?? p?.Name ?? "?"; } catch { return "?"; }
    }

    static string Clock() => $"Day {GameTime.Day} {GameTime.Hour:00}:{GameTime.Minute:00}";

    static string Hour(float h) => $"{(int)h % 24:00}:{(int)Math.Round(h % 1f * 60f):00}";

    // ---------- saving ----------

    sealed class Saved
    {
        public Dictionary<string, Book> Books { get; set; }
        public Dictionary<string, int> Costs { get; set; }
        public Dictionary<string, StockLine> Pocket { get; set; }
        public bool PocketOpened { get; set; }
        public Dictionary<string, Waste> PocketWasted { get; set; }
        public Dictionary<string, int> Grown { get; set; }
        public int NextBatchId { get; set; }
    }

    static void Load()
    {
        var saved = SaveData.For(Owner).Get<Saved>("ledger");
        books = saved?.Books ?? new();
        costOverrides = saved?.Costs ?? new();
        pocket = saved?.Pocket ?? new();
        pocketOpened = saved?.PocketOpened ?? false;
        pocketWasted = saved?.PocketWasted ?? new();
        grown = saved?.Grown ?? new();
        nextBatchId = Math.Max(1, saved?.NextBatchId ?? 1);
        feed.Clear(); openOrders.Clear(); doneOrders.Clear(); lastToday.Clear(); pastCache.Clear(); names.Clear(); rowsCache.Clear();
        snapshotAt = 0f;
    }

    static void Save() => SaveData.For(Owner).Set("ledger", new Saved
    {
        Books = books, Costs = costOverrides, Pocket = pocket, PocketOpened = pocketOpened, PocketWasted = pocketWasted, Grown = grown, NextBatchId = nextBatchId,
    });
}
