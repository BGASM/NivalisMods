using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Nivalis;
using NivalisModKit;
using SCG = System.Collections.Generic;

namespace NivalisOrderFix;

public enum SortMode { Vanilla, Cheapest, Local, Balanced }

// The game buys what's needed from its vendor list; the kit's purchasing pipeline (on once this mod adds a
// VendorOrdering handler) lets this mod set that list's order. It also shows what it did: a page in the kit's Mods
// browser, a dev command, and an optional verbose log. Written as an example of a kit mod; see the README.
[BepInPlugin(Guid, "Better Supplier Choice", "2.2.0")]   // formerly Manager Order Fix; GUID, config file and DLL name unchanged
[BepInDependency(ModKit.Guid, ">=0.6.2")]   // the game's patch 4 restock
public class Plugin : BasePlugin
{
    const string Guid = "bgasm.nivalis.orderfix";

    internal static ManualLogSource L;
    static ConfigEntry<bool> Verbose;
    static ConfigEntry<SortMode> VendorSort;
    static ConfigEntry<float> DistanceWeight;
    static ConfigEntry<float> ScarcityWeight;

    const int Unreachable = 99;

    public override void Load()
    {
        L = Log;
        MigrateOldConfig();

        // Settings. Every one is read when it's used, so changes apply live (in the kit's Mods browser, or by
        // saving the .cfg). The ranges make the browser show sliders.
        VendorSort = Config.Bind("General", "VendorSort", SortMode.Vanilla,
            "Vendor order when filling an order. Vanilla: the game's order (cheapest first, then most stock). " +
            "Cheapest: lowest price first. Local: nearest district first. Balanced: weighs price, distance and stock.");
        DistanceWeight = Config.Bind("Balanced", "DistanceWeight", 0.07f, new ConfigDescription(
            "Balanced mode: price penalty per district hop. 0.07 = +7% per hop. " +
            "Higher prefers nearby vendors; lower chases discounts further away.",
            new AcceptableValueRange<float>(0f, 0.5f)));
        ScarcityWeight = Config.Bind("Balanced", "ScarcityWeight", 2.0f, new ConfigDescription(
            "Balanced mode: penalty for low stock, as price x (1 + ScarcityWeight / stock).",
            new AcceptableValueRange<float>(0f, 10f)));
        Verbose = Config.Bind("Debug", "Verbose", false, new ConfigDescription(
            "Log every restock round: the vendors bought from, and each skipped vendor's price, stock and hops.",
            null, new ConfigurationManagerAttributes { IsAdvanced = true }));
        ModMenu.ListSettings(Guid);

        // The game buys what's needed, from vendors in the order this handler sets.
        Purchasing.VendorOrdering += OrderVendors;
        Purchasing.Decision += Record;
        GameEvents.BuyIngredientsFinished += FinishRound;

        // What it did today, in the Mods browser and as a dev command; counts restart each game day.
        ModMenu.AddPage(Guid, "Better Supplier Choice", BuildPage);
        DevCommands.Register(Guid, "orderfix", "[mode=Vanilla|Cheapest|Local|Balanced]: today's results; mode= switches the vendor order", RunCommand);
        GameEvents.DayStarted += _ => today = new Stats();
        GameEvents.GameLoaded += _ => today = new Stats();
        GameEvents.NewGameStarted += () => today = new Stats();

        if (Purchasing.IsAvailable)
            L.LogInfo($"Better Supplier Choice loaded, VendorSort = {VendorSort.Value}");
        else
            L.LogError("Better Supplier Choice: the kit's purchasing pipeline is unavailable, vendor order inactive");
    }

    // 1.x used the GUID will.nivalis.orderfix, so its settings are in that file. Copy them to the
    // new file once, before binding. The old file is left in place.
    void MigrateOldConfig()
    {
        try
        {
            string oldPath = Path.Combine(Paths.ConfigPath, "will.nivalis.orderfix.cfg");
            if (File.Exists(Config.ConfigFilePath) || !File.Exists(oldPath)) return;
            File.Copy(oldPath, Config.ConfigFilePath);
            Config.Reload();
            L.LogInfo("Copied settings from will.nivalis.orderfix.cfg (Manager Order Fix 1.x); the old file can be deleted");
        }
        catch (Exception e) { L.LogWarning($"Could not copy 1.x settings, using defaults: {e.Message}"); }
    }

    // ---------- vendor order ----------

    static int Hops(VendorOffer o) => o.Hops == World.Unreachable ? Unreachable : o.Hops;

    static double Score(VendorOffer o) =>
        o.Price
        * (1.0 + DistanceWeight.Value * Hops(o))
        * (1.0 + ScarcityWeight.Value / Math.Max(o.Stock, 1));

    static void OrderVendors(VendorOrderingContext ctx)
    {
        var list = ctx.Offers;
        switch (VendorSort.Value)
        {
            case SortMode.Cheapest:
                ctx.Offers = list.OrderBy(o => o.Price).ThenByDescending(o => o.Stock).ThenBy(o => o.Sequence).ToList();
                break;
            case SortMode.Local:
                ctx.Offers = list.OrderBy(Hops).ThenByDescending(o => o.Stock).ThenBy(o => o.Price).ThenBy(o => o.Sequence).ToList();
                break;
            case SortMode.Balanced:
                ctx.Offers = list.OrderBy(Score).ThenByDescending(o => o.Stock).ThenBy(o => o.Sequence).ToList();
                break;
            default: // Vanilla: the game's own order
                break;
        }
    }

    // ---------- what it did: per round, then per day ----------

    sealed class Stats
    {
        public int Orders, NpcOrders;
        public long Spent, GameWouldSpend;   // hundredths; the player's venues only
    }
    static Stats today = new();

    // The kit reports every offer of a recipe's round (bought, skipped or failed) before BuyIngredientsFinished.
    static readonly SCG.List<PurchaseDecisionArgs> round = new();

    static void Record(PurchaseDecisionArgs d) => round.Add(d);

    static void FinishRound(BuyIngredientsArgs a)
    {
        if (round.Count == 0) return;
        try
        {
            bool mine = a.Area != null && a.Area.PlayerOwned;
            foreach (var item in round.GroupBy(d => d.Offer.Item.Pointer))
            {
                var offers = item.ToList();
                var bought = offers.Where(d => d.Result == PurchaseResult.Bought && d.Amount > 0).ToList();
                if (bought.Count == 0) continue;
                if (!mine) { today.NpcOrders++; continue; }
                // What the game would have paid: the same amount from its own first choice (lowest Sequence).
                var gameFirst = offers.OrderBy(d => d.Offer.Sequence).First().Offer;
                today.Orders++;
                foreach (var b in bought)
                {
                    today.Spent += (long)b.Offer.Price * b.Amount;
                    today.GameWouldSpend += (long)gameFirst.Price * b.Amount;
                }
            }
            if (Verbose.Value) LogRound();
        }
        catch (Exception e) { L.LogWarning($"Better Supplier Choice stats: {e.Message}"); }
        finally { round.Clear(); }
    }

    // One line per vendor bought from; one line per run of skipped vendors, still listing each one's
    // price/stock/hops so the sort can be checked (tools/check_orderfix.py reads these lines).
    static void LogRound()
    {
        foreach (var item in round.GroupBy(d => d.Offer.Item.Pointer))
        {
            var offers = item.ToList();
            string name = Items.NameOf(offers[0].Offer.Item) ?? "?";
            var skipped = new SCG.List<VendorOffer>();
            void Flush()
            {
                if (skipped.Count == 0) return;
                L.LogInfo($"Skip {name} at {skipped.Count} vendor{(skipped.Count == 1 ? "" : "s")} (order filled): " +
                          string.Join(", ", skipped.Select(o => $"{Economy.NameOf(o.Vendor)} {o.Price}/{o.Stock}/{Hops(o)}")));
                skipped.Clear();
            }
            foreach (var d in offers)
            {
                var o = d.Offer;
                if (d.Result == PurchaseResult.Skipped) { skipped.Add(o); continue; }
                Flush();
                L.LogInfo($"Buy {name} x{d.Amount} at {Economy.NameOf(o.Vendor)} " +
                          $"(price {o.Price}, stock {o.Stock}, hops {Hops(o)}, score {Score(o):0}) " +
                          $"[{VendorSort.Value}, {d.OfferCount} vendors]");
                if (d.Result == PurchaseResult.Failed)
                    L.LogInfo($"Purchase failed for {name} at {Economy.NameOf(o.Vendor)}; the game moves to the next vendor.");
            }
            Flush();
        }
    }

    // ---------- the Mods browser page and the dev command ----------

    static readonly string[] ModeHelp =
    {
        "Vanilla: the game's own order, cheapest first, then most stock.",
        "Cheapest: lowest price first (the same as the game's order now).",
        "Local: nearest district first.",
        "Balanced: price, weighed against distance and low stock.",
    };

    static string Money(long hundredths) => (hundredths / 100.0).ToString("0.00");

    static string Difference()
    {
        long d = today.GameWouldSpend - today.Spent;
        return d == 0 ? "same" : d > 0 ? $"saved {Money(d)}" : $"{Money(-d)} more";
    }

    static void BuildPage(KitWindow w)
    {
        w.AddHeader("Vendor order");
        w.AddChoice("Mode", Enum.GetNames(typeof(SortMode)), (int)VendorSort.Value, i =>
        {
            VendorSort.Value = (SortMode)i;   // saved to the .cfg like any setting change
            w.Clear();
            BuildPage(w);                     // redraw with the new mode's description
        });
        w.AddText(ModeHelp[(int)VendorSort.Value]);
        w.AddHeader("Today, your venues");
        w.AddValue("Ingredient orders", today.Orders.ToString());
        w.AddValue("Spent", Money(today.Spent));
        var diff = w.AddValue("Vs the game's order", Difference());
        if (diff != null)
            Ui.Tooltip(diff.transform.parent.gameObject,
                "What the same amounts would have cost from the vendor the game picks first (the cheapest). " +
                "Local and Balanced can cost a little more: they trade price for nearer or better-stocked vendors.");
        w.AddText($"City venues ordered {today.NpcOrders} times today, also in this order.");
    }

    static object RunCommand(CommandArgs a)
    {
        if (a.Has("mode"))
        {
            if (!Enum.TryParse<SortMode>(a.Get("mode"), true, out var m))
                throw new ArgumentException($"mode must be one of: {string.Join(", ", Enum.GetNames(typeof(SortMode)))}");
            VendorSort.Value = m;
        }
        return new
        {
            mode = VendorSort.Value.ToString(),
            orders = today.Orders,
            spent = Money(today.Spent),
            gameWouldSpend = Money(today.GameWouldSpend),
            difference = Difference(),
            cityVenueOrders = today.NpcOrders,
        };
    }
}
