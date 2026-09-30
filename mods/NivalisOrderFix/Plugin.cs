using System;
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

// The fix itself (buy what's needed and stop) is the kit's purchasing pipeline, which turns on
// when this mod adds a VendorOrdering handler. This mod supplies the vendor order.
[BepInPlugin("will.nivalis.orderfix", "Manager Order Fix", "2.0.0")]
[BepInDependency(ModKit.Guid)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    static ConfigEntry<bool> Verbose;
    static ConfigEntry<SortMode> VendorSort;
    static ConfigEntry<float> DistanceWeight;
    static ConfigEntry<float> ScarcityWeight;

    const int Unreachable = 99;

    static readonly SCG.HashSet<IntPtr> HopTablesLogged = new();   // starts already reported

    public override void Load()
    {
        L = Log;
        VendorSort = Config.Bind("General", "VendorSort", SortMode.Vanilla,
            "Vendor order when filling an order. Vanilla: most stock first (the game's intent). " +
            "Cheapest: lowest price first. Local: nearest district first. Balanced: weighs price, distance and stock.");
        DistanceWeight = Config.Bind("Balanced", "DistanceWeight", 0.07f,
            "Balanced mode: price penalty per district hop. 0.07 = +7% per hop. " +
            "Higher prefers nearby vendors; lower chases discounts further away.");
        ScarcityWeight = Config.Bind("Balanced", "ScarcityWeight", 2.0f,
            "Balanced mode: penalty for low stock, as price x (1 + ScarcityWeight / stock).");
        Verbose = Config.Bind("Debug", "Verbose", false,
            "Log each vendor purchase the fix makes or skips.");

        Purchasing.VendorOrdering += OrderVendors;
        Purchasing.Decision += LogDecision;

        if (Purchasing.IsAvailable)
            L.LogInfo($"Manager Order Fix loaded, VendorSort = {VendorSort.Value}");
        else
            L.LogError("Manager Order Fix: the kit's purchasing pipeline is unavailable, fix inactive");
    }

    // ---------- helpers ----------

    static string NameOf(Il2CppSystem.Object obj)
    {
        if (obj == null) return "?";
        try
        {
            string s = obj.ToString();
            int i = s.IndexOf(" (", StringComparison.Ordinal);
            return i > 0 ? s.Substring(0, i) : s;
        }
        catch { return "?"; }
    }

    static int Hops(VendorOffer o) => o.Hops == World.Unreachable ? Unreachable : o.Hops;

    static double Score(VendorOffer o) =>
        o.Price
        * (1.0 + DistanceWeight.Value * Hops(o))
        * (1.0 + ScarcityWeight.Value / Math.Max(o.Stock, 1));

    static void LogHopTable(WorldLocation start)
    {
        if (start == null || !HopTablesLogged.Add(start.Pointer)) return;
        int reachable = World.Locations.Count(l => World.Hops(start, l) != World.Unreachable);
        L.LogInfo($"Built hop table from {NameOf(start)}: {reachable} districts reachable");
    }

    // ---------- vendor order ----------

    static void OrderVendors(VendorOrderingContext ctx)
    {
        if (Verbose.Value) LogHopTable(ctx.VenueLocation);

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
            default: // Vanilla
                ctx.Offers = list.OrderByDescending(o => o.Stock).ThenBy(o => o.Sequence).ToList();
                break;
        }
    }

    // ---------- verbose log, same lines as 1.x ----------

    static void LogDecision(PurchaseDecisionArgs d)
    {
        if (!Verbose.Value) return;
        var o = d.Offer;
        string item = NameOf(o.Item), vendor = NameOf(o.Vendor);

        if (d.Result == PurchaseResult.Skipped)
        {
            L.LogInfo($"Skip {item} at {vendor} (price {o.Price}, stock {o.Stock}, hops {Hops(o)}): order filled");
            return;
        }

        L.LogInfo($"Buy {item} x{d.Amount} at {vendor} " +
                  $"(price {o.Price}, stock {o.Stock}, hops {Hops(o)}, score {Score(o):0}) " +
                  $"[{VendorSort.Value}, {d.OfferCount} vendors]");

        if (d.Result == PurchaseResult.Failed)
            L.LogInfo($"Purchase failed for {item} at {vendor} " +
                      "(likely out of money). Stopping this recipe's purchases.");
    }
}
