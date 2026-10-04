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

// The fix itself (buy what's needed and stop) is the kit's purchasing pipeline, which turns on
// when this mod adds a VendorOrdering handler. This mod supplies the vendor order.
[BepInPlugin("bgasm.nivalis.orderfix", "Manager Order Fix", "2.1.0")]
[BepInDependency(ModKit.Guid, ">=0.2.0")]
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
        MigrateOldConfig();
        VendorSort = Config.Bind("General", "VendorSort", SortMode.Vanilla,
            "Vendor order when filling an order. Vanilla: most stock first (the game's intent). " +
            "Cheapest: lowest price first. Local: nearest district first. Balanced: weighs price, distance and stock.");
        DistanceWeight = Config.Bind("Balanced", "DistanceWeight", 0.07f, new ConfigDescription(
            "Balanced mode: price penalty per district hop. 0.07 = +7% per hop. " +
            "Higher prefers nearby vendors; lower chases discounts further away.",
            new AcceptableValueRange<float>(0f, 0.5f)));
        ScarcityWeight = Config.Bind("Balanced", "ScarcityWeight", 2.0f, new ConfigDescription(
            "Balanced mode: penalty for low stock, as price x (1 + ScarcityWeight / stock).",
            new AcceptableValueRange<float>(0f, 10f)));
        Verbose = Config.Bind("Debug", "Verbose", false, new ConfigDescription(
            "Log each vendor purchase the fix makes or skips.", null, new ModSetting { IsAdvanced = true }));
        // All settings in the kit's in-game browser (pause menu > Mods); each is read when used, so changes apply live.
        ModMenu.ListSettings("bgasm.nivalis.orderfix");

        Purchasing.VendorOrdering += OrderVendors;
        Purchasing.Decision += LogDecision;

        if (Purchasing.IsAvailable)
            L.LogInfo($"Manager Order Fix loaded, VendorSort = {VendorSort.Value}");
        else
            L.LogError("Manager Order Fix: the kit's purchasing pipeline is unavailable, fix inactive");
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
            L.LogInfo("Copied settings from will.nivalis.orderfix.cfg (Order Fix 1.x); the old file can be deleted");
        }
        catch (Exception e) { L.LogWarning($"Could not copy 1.x settings, using defaults: {e.Message}"); }
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
