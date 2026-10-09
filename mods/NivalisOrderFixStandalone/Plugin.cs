using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.Economy;
using Nivalis.GhostSystem.CustomerLoop;
using SCG = System.Collections.Generic;

namespace NivalisOrderFixStandalone;

public enum SortMode { Vanilla, Cheapest, Local, Balanced }

// The managers' purchase loop (TryPurchaseIngredients) walks the list from EconomyManager.GetVendorsByItem,
// (vendor, stock, price) sorted cheapest first since the game's October 9 patch, and buys until each
// ingredient's need is met. Sorting that list is all this mod needs to do. Only lists built inside
// TryPurchaseIngredients are sorted; furniture buying uses the same method and is left alone.
//
// Same GUID as the ModKit edition: BepInEx loads only one, and both share bgasm.nivalis.orderfix.cfg.
[BepInPlugin("bgasm.nivalis.orderfix", "Better Supplier Choice (Standalone)", "1.2.0")]   // formerly Manager Order Fix
public unsafe class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    static ConfigEntry<bool> Verbose;
    static ConfigEntry<SortMode> VendorSort;
    static ConfigEntry<float> DistanceWeight;
    static ConfigEntry<float> ScarcityWeight;

    const int Unreachable = 99;

    // ---------- per-recipe context (set by the TryPurchaseIngredients prefix) ----------
    static IntPtr CurrentGhost = IntPtr.Zero;
    static WorldLocation CurrentVenueLoc;

    sealed class Offer
    {
        public IntPtr Vendor;
        public int Price, Stock, Hops, Seq;
        public double Score;
        public int Bought, Failed;
    }

    sealed class ItemRound
    {
        public IntPtr Item;
        public SCG.List<Offer> Offers;
        public SortMode Mode;
    }

    // Per ingredient, the vendors in buying order and what each sold (Verbose only).
    static readonly SCG.List<ItemRound> Rounds = new();
    static ItemRound CurrentItem;

    // ---------- session caches ----------
    static readonly SCG.Dictionary<IntPtr, IntPtr> VendorLoc = new();                       // vendor -> WorldLocation
    static readonly SCG.Dictionary<IntPtr, SCG.Dictionary<IntPtr, int>> HopCache = new();   // from -> (to -> hops)

    // ---------- native hooks ----------
    // GetVendorsByItem returns a ListPool.Handle struct through a hidden pointer and the list through an
    // out parameter; BuyItem takes by-ref structs. Harmony can't wrap those safely under IL2CPP.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetVendorsFn(IntPtr ret, IntPtr self, IntPtr item, IntPtr resultsRef, IntPtr method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate byte BuyItemFn(IntPtr self, IntPtr request, float discount, IntPtr tempItem, IntPtr method);

    static GetVendorsFn HookDelegate, Original;
    static BuyItemFn BuyDelegate, OriginalBuy;
    static INativeDetour Detour, BuyDetour;

    // Layout of List<ValueTuple<Vendor, int, int>>, read from the IL2CPP runtime on first use.
    static bool layoutKnown;
    static int OffItems, OffSize, ElemSize, OffVendor, OffStock, OffPrice;
    static readonly int ArrayData = 4 * IntPtr.Size;   // Il2CppArray: klass, monitor, bounds, max_length
    static int OffCustomer = -1, OffItemType, OffAmount;   // ShopTradeRequest, for Verbose

    public override void Load()
    {
        L = Log;
        MigrateOldConfig();
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
            "Log each vendor purchase and skip.", null, new ConfigurationManagerAttributes { IsAdvanced = true }));

        try
        {
            Detour = Hook<GetVendorsFn>(typeof(EconomyManager), "GetVendorsByItem", HookDelegate = GetVendorsHook, out Original);
        }
        catch (Exception e)
        {
            L.LogError($"Better Supplier Choice could not hook GetVendorsByItem, inactive: {e}");
            return;
        }
        try { Harmony.CreateAndPatchAll(typeof(Plugin)); }
        catch (Exception e)
        {
            L.LogError($"Better Supplier Choice could not patch TryPurchaseIngredients, inactive: {e.Message}");
            return;
        }
        // Only feeds Verbose logging; if it fails the vendor sort still works.
        try
        {
            IntPtr req = Il2CppClassPointerStore<ShopTradeRequest>.NativeClassPtr;
            OffCustomer = FieldOffset(req, "customer") - 2 * IntPtr.Size;
            OffItemType = FieldOffset(req, "itemType") - 2 * IntPtr.Size;
            OffAmount = FieldOffset(req, "amount") - 2 * IntPtr.Size;
            // BuyItem has overloads; this is the managers' one (Boolean, ShopTradeRequest, Single, BasicTemp).
            BuyDetour = Hook<BuyItemFn>(typeof(Vendor), "BuyItem_Public_Boolean_byref_ShopTradeRequest_Single_byref_BasicTemp", BuyDelegate = BuyItemHook, out OriginalBuy);
        }
        catch (Exception e) { L.LogWarning($"Better Supplier Choice: Verbose purchase logging unavailable ({e.Message})"); }
        L.LogInfo($"Better Supplier Choice loaded, VendorSort = {VendorSort.Value}");
    }

    // 1.0 used the GUID will.nivalis.orderfix, so its settings are in that file. Copy them to the
    // new file once, before binding. The old file is left in place.
    void MigrateOldConfig()
    {
        try
        {
            string oldPath = Path.Combine(Paths.ConfigPath, "will.nivalis.orderfix.cfg");
            if (File.Exists(Config.ConfigFilePath) || !File.Exists(oldPath)) return;
            File.Copy(oldPath, Config.ConfigFilePath);
            Config.Reload();
            L.LogInfo("Copied settings from will.nivalis.orderfix.cfg (Manager Order Fix 1.0); the old file can be deleted");
        }
        catch (Exception e) { L.LogWarning($"Could not copy 1.0 settings, using defaults: {e.Message}"); }
    }

    // ---------- setup ----------

    // Detours the native code of an interop method with one overload.
    static INativeDetour Hook<T>(Type type, string method, T hook, out T original) where T : Delegate
    {
        RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        var fields = type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(f => f.Name.StartsWith($"NativeMethodInfoPtr_{method}_", StringComparison.Ordinal)).ToList();
        if (fields.Count != 1) throw new Exception($"{type.Name}.{method}: {fields.Count} matches");
        IntPtr methodInfo = (IntPtr)fields[0].GetValue(null);
        if (methodInfo == IntPtr.Zero) throw new Exception($"{type.Name}.{method} method info is null");
        return INativeDetour.CreateAndApply(*(IntPtr*)methodInfo, hook, out original);
    }

    static int FieldOffset(IntPtr klass, string name)
    {
        IntPtr f = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
        if (f == IntPtr.Zero) throw new Exception($"field {name} not found");
        return (int)IL2CPP.il2cpp_field_get_offset(f);
    }

    // Reads the list layout from a live list: the List<T> fields, then T = ValueTuple<Vendor, int, int>.
    static void LearnLayout(IntPtr list)
    {
        IntPtr listClass = IL2CPP.il2cpp_object_get_class(list);
        OffItems = FieldOffset(listClass, "_items");
        OffSize = FieldOffset(listClass, "_size");

        IntPtr items = *(IntPtr*)(list + OffItems);
        if (items == IntPtr.Zero) throw new Exception("list has no backing array");
        IntPtr arrayClass = IL2CPP.il2cpp_object_get_class(items);
        ElemSize = IL2CPP.il2cpp_array_element_size(arrayClass);
        IntPtr elemClass = IL2CPP.il2cpp_class_get_element_class(arrayClass);
        // Field offsets of a value type include the object header; array elements don't have one.
        OffVendor = FieldOffset(elemClass, "Item1") - 2 * IntPtr.Size;
        OffStock = FieldOffset(elemClass, "Item2") - 2 * IntPtr.Size;
        OffPrice = FieldOffset(elemClass, "Item3") - 2 * IntPtr.Size;

        layoutKnown = true;
        if (Verbose.Value)
            L.LogInfo($"Hooked GetVendorsByItem: items={OffItems} size={OffSize} elem={ElemSize} " +
                      $"vendor={OffVendor} stock={OffStock} price={OffPrice}");
    }

    static string NameOf(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return "?";
        try
        {
            string s = new Il2CppSystem.Object(obj).ToString();
            int i = s.IndexOf(" (", StringComparison.Ordinal);
            return i > 0 ? s.Substring(0, i) : s;
        }
        catch { return "?"; }
    }

    // ---------- distance ----------

    static SCG.Dictionary<IntPtr, int> HopTable(WorldLocation start)
    {
        if (HopCache.TryGetValue(start.Pointer, out var table)) return table;

        table = new SCG.Dictionary<IntPtr, int> { [start.Pointer] = 0 };
        var queue = new SCG.Queue<WorldLocation>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            int d = table[cur.Pointer];
            var links = cur.transitions;
            if (links == null) continue;

            for (int i = 0; i < links.Count; i++)
            {
                var to = links[i]?.ToLocation;
                if (to == null || table.ContainsKey(to.Pointer)) continue;
                table[to.Pointer] = d + 1;
                queue.Enqueue(to);
            }
        }

        HopCache[start.Pointer] = table;
        if (Verbose.Value)
            L.LogInfo($"Built hop table from {NameOf(start.Pointer)}: {table.Count} districts reachable");
        return table;
    }

    static int HopsTo(IntPtr vendor)
    {
        if (CurrentVenueLoc == null) return Unreachable;

        if (!VendorLoc.TryGetValue(vendor, out IntPtr loc))
        {
            try { loc = new Vendor(vendor).Location?.Location?.Pointer ?? IntPtr.Zero; }
            catch { loc = IntPtr.Zero; }
            VendorLoc[vendor] = loc;
        }
        if (loc == IntPtr.Zero) return Unreachable;

        return HopTable(CurrentVenueLoc).TryGetValue(loc, out int h) ? h : Unreachable;
    }

    // ---------- the hook: sort the vendor list ----------

    static IntPtr GetVendorsHook(IntPtr ret, IntPtr self, IntPtr item, IntPtr resultsRef, IntPtr method)
    {
        IntPtr r = Original(ret, self, item, resultsRef, method);
        if (CurrentGhost == IntPtr.Zero || resultsRef == IntPtr.Zero) return r;   // not ingredient buying
        try
        {
            IntPtr list = *(IntPtr*)resultsRef;
            if (list != IntPtr.Zero) Sort(list, item);
        }
        catch (Exception e)
        {
            L.LogError($"GetVendorsHook: {e.Message}");
        }
        return r;
    }

    static void Sort(IntPtr list, IntPtr item)
    {
        if (!layoutKnown) LearnLayout(list);

        int count = *(int*)(list + OffSize);
        if (count == 0) return;
        IntPtr items = *(IntPtr*)(list + OffItems);
        byte* data = (byte*)items + ArrayData;

        var offers = new SCG.List<Offer>(count);
        for (int i = 0; i < count; i++)
        {
            byte* e = data + i * ElemSize;
            IntPtr vendor = *(IntPtr*)(e + OffVendor);
            var o = new Offer { Vendor = vendor, Stock = *(int*)(e + OffStock), Price = *(int*)(e + OffPrice), Seq = i };
            o.Hops = HopsTo(vendor);
            o.Score = o.Price
                    * (1.0 + DistanceWeight.Value * o.Hops)
                    * (1.0 + ScarcityWeight.Value / Math.Max(o.Stock, 1));
            offers.Add(o);
        }

        SortMode mode = VendorSort.Value;
        var sorted = Order(offers, mode);

        if (mode != SortMode.Vanilla)
        {
            // Write the elements back in the new order. Vendor references go through the GC write
            // barrier; stock and price are plain ints.
            for (int i = 0; i < count; i++)
            {
                byte* e = data + i * ElemSize;
                IL2CPP.il2cpp_gc_wbarrier_set_field(items, (IntPtr)(e + OffVendor), sorted[i].Vendor);
                *(int*)(e + OffStock) = sorted[i].Stock;
                *(int*)(e + OffPrice) = sorted[i].Price;
            }
        }

        if (Verbose.Value)
        {
            CurrentItem = new ItemRound { Item = item, Offers = sorted, Mode = mode };
            Rounds.Add(CurrentItem);
        }
    }

    static SCG.List<Offer> Order(SCG.List<Offer> list, SortMode mode)
    {
        switch (mode)
        {
            case SortMode.Cheapest:
                return list.OrderBy(p => p.Price).ThenByDescending(p => p.Stock).ThenBy(p => p.Seq).ToList();
            case SortMode.Local:
                return list.OrderBy(p => p.Hops).ThenByDescending(p => p.Stock).ThenBy(p => p.Price).ThenBy(p => p.Seq).ToList();
            case SortMode.Balanced:
                return list.OrderBy(p => p.Score).ThenByDescending(p => p.Stock).ThenBy(p => p.Seq).ToList();
            default: // Vanilla: the game's order
                return list;
        }
    }

    // ---------- Verbose: what the game bought from each vendor ----------

    static byte BuyItemHook(IntPtr self, IntPtr request, float discount, IntPtr tempItem, IntPtr method)
    {
        byte ok = OriginalBuy(self, request, discount, tempItem, method);
        try
        {
            var r = CurrentItem;
            if (r == null || OffCustomer < 0 || request == IntPtr.Zero ||
                *(IntPtr*)(request + OffCustomer) != CurrentGhost || *(IntPtr*)(request + OffItemType) != r.Item)
                return ok;
            var o = r.Offers.Find(x => x.Vendor == self);
            if (o != null)
            {
                int amount = *(int*)(request + OffAmount);
                if (ok != 0) o.Bought += amount; else o.Failed += amount;
            }
        }
        catch { }
        return ok;
    }

    // In buying order: one line per vendor bought from (or refused), and one line per run of vendors not
    // used, still listing each one's price/stock/hops to check the sort math. A vendor isn't used when the
    // need was already met, or when the game stopped (the venue couldn't afford it, or its restock budget ran out).
    static void LogRound()
    {
        foreach (var r in Rounds)
        {
            string name = NameOf(r.Item);
            int n = r.Offers.Count;
            var run = new SCG.List<Offer>();
            void Flush()
            {
                if (run.Count == 0) return;
                L.LogInfo($"Skip {name} at {run.Count} vendor{(run.Count == 1 ? "" : "s")} (not needed or over budget): " +
                          string.Join(", ", run.Select(o => $"{NameOf(o.Vendor)} {o.Price}/{o.Stock}/{o.Hops}")));
                run.Clear();
            }
            foreach (var o in r.Offers)
            {
                if (o.Bought == 0 && o.Failed == 0) { run.Add(o); continue; }
                Flush();
                if (o.Bought > 0)
                    L.LogInfo($"Buy {name} x{o.Bought} at {NameOf(o.Vendor)} " +
                              $"(price {o.Price}, stock {o.Stock}, hops {o.Hops}, score {o.Score:0}) [{r.Mode}, {n} vendors]");
                if (o.Failed > 0)
                    L.LogInfo($"Purchase failed for {name} x{o.Failed} at {NameOf(o.Vendor)}; the game moved to the next vendor.");
            }
            Flush();
        }
    }

    // ---------- recipe context (Harmony, safe signatures) ----------

    static void Reset()
    {
        Rounds.Clear();
        CurrentItem = null;
        CurrentGhost = IntPtr.Zero;
        CurrentVenueLoc = null;
    }

    [HarmonyPatch(typeof(VenueAreaGhost), nameof(VenueAreaGhost.TryPurchaseIngredients))]
    [HarmonyPrefix]
    static void RecipePre(VenueAreaGhost __instance)
    {
        Reset();
        if (__instance == null || Detour == null) return;
        CurrentGhost = __instance.Pointer;
        try { CurrentVenueLoc = __instance.Venue?.Location; }
        catch { CurrentVenueLoc = null; }
    }

    [HarmonyPatch(typeof(VenueAreaGhost), nameof(VenueAreaGhost.TryPurchaseIngredients))]
    [HarmonyPostfix]
    static void RecipePost()
    {
        try
        {
            if (Verbose.Value && Rounds.Count > 0) LogRound();
        }
        catch (Exception e) { L.LogError($"RecipePost: {e.Message}"); }
        finally { Reset(); }
    }
}

// The standard BepInEx settings tag, copied in as mods do: settings menus (Mod Settings Menu, Configuration
// Manager, the ModKit's browser) find it by name and read these fields.
#pragma warning disable 0649
internal sealed class ConfigurationManagerAttributes
{
    public bool? IsAdvanced;
    public bool? Browsable;
    public bool? ReadOnly;
    public int? Order;
    public string DispName;
}
#pragma warning restore 0649
