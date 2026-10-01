using System;
using System.Linq;
using System.Reflection;
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
using Nivalis.InventorySystem;
using IL2List = Il2CppSystem.Collections.Generic.List<Nivalis.InventorySystem.ItemInstanceData>;
using SCG = System.Collections.Generic;

namespace NivalisOrderFixStandalone;

public enum SortMode { Vanilla, Cheapest, Local, Balanced }

// Same GUID as the ModKit edition: BepInEx loads only one, and both share bgasm.nivalis.orderfix.cfg.
[BepInPlugin("bgasm.nivalis.orderfix", "Manager Order Fix (Standalone)", "1.0.0")]
public unsafe class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    static ConfigEntry<bool> Verbose;
    static ConfigEntry<SortMode> VendorSort;
    static ConfigEntry<float> DistanceWeight;
    static ConfigEntry<float> ScarcityWeight;

    const int Unreachable = 99;

    // ---------- per-recipe context ----------
    static IntPtr CurrentGhost = IntPtr.Zero;
    static WorldLocation CurrentVenueLoc;
    static readonly SCG.Dictionary<IntPtr, int> ToBuy = new();
    static readonly SCG.Dictionary<IntPtr, int> Bought = new();

    sealed class Pending
    {
        public IntPtr Vendor, Req, Temp, Method;
        public float Discount;
        public int Amount, Price, Stock, Hops, Seq;
        public double Score;
    }
    static readonly SCG.Dictionary<IntPtr, SCG.List<Pending>> PendingByItem = new();
    static readonly SCG.List<IntPtr> PendingOrder = new();
    static int Seq;

    // ---------- session caches ----------
    static readonly SCG.Dictionary<IntPtr, IntPtr> VendorLoc = new();                       // vendor -> WorldLocation
    static readonly SCG.Dictionary<IntPtr, SCG.Dictionary<IntPtr, int>> HopCache = new();   // from -> (to -> hops)

    // ---------- native hook on Vendor.BuyItem(ref request, float, ref BasicTemp) ----------
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void BuyItemFn(IntPtr self, IntPtr request, float discount, IntPtr tempItem, IntPtr method);

    static BuyItemFn HookDelegate;
    static BuyItemFn Original;
    static INativeDetour Detour;

    static int OffCustomer, OffItemType, OffFreshness, OffAmount, OffStackCount;
    static int ReqSize, TempSize;

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

        Harmony.CreateAndPatchAll(typeof(Plugin));

        try
        {
            InstallBuyItemHook();
            L.LogInfo($"Manager Order Fix loaded, VendorSort = {VendorSort.Value}");
        }
        catch (Exception e)
        {
            L.LogError($"Manager Order Fix could not hook BuyItem, fix inactive: {e}");
        }
    }

    // ---------- setup ----------

    static IntPtr ClassPtr<T>()
    {
        RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
        return Il2CppClassPointerStore<T>.NativeClassPtr;
    }

    static int ValueFieldOffset<T>(string field)
    {
        IntPtr f = IL2CPP.GetIl2CppField(ClassPtr<T>(), field);
        if (f == IntPtr.Zero) throw new Exception($"Field {typeof(T).Name}.{field} not found");
        return (int)IL2CPP.il2cpp_field_get_offset(f) - 2 * IntPtr.Size;
    }

    static int ValueSize<T>()
    {
        uint align = 0;
        return (int)IL2CPP.il2cpp_class_value_size(ClassPtr<T>(), ref align);
    }

    static void InstallBuyItemHook()
    {
        OffCustomer   = ValueFieldOffset<ShopTradeRequest>("customer");
        OffItemType   = ValueFieldOffset<ShopTradeRequest>("itemType");
        OffFreshness  = ValueFieldOffset<ShopTradeRequest>("freshness");
        OffAmount     = ValueFieldOffset<ShopTradeRequest>("amount");
        OffStackCount = ValueFieldOffset<ItemStack.BasicTemp>("StackCount");
        ReqSize       = ValueSize<ShopTradeRequest>();
        TempSize      = ValueSize<ItemStack.BasicTemp>();

        RuntimeHelpers.RunClassConstructor(typeof(Vendor).TypeHandle);
        var infoField = typeof(Vendor).GetField(
            "NativeMethodInfoPtr_BuyItem_Public_Void_byref_ShopTradeRequest_Single_byref_BasicTemp_0",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (infoField == null) throw new Exception("BuyItem method info field not found");

        IntPtr methodInfo = (IntPtr)infoField.GetValue(null);
        if (methodInfo == IntPtr.Zero) throw new Exception("BuyItem method info is null");
        IntPtr methodPtr = Marshal.ReadIntPtr(methodInfo);

        HookDelegate = BuyItemHook;
        Detour = INativeDetour.CreateAndApply(methodPtr, HookDelegate, out Original);

        if (Verbose.Value)
            L.LogInfo($"Hooked BuyItem at 0x{methodPtr.ToInt64():X}; customer={OffCustomer} " +
                      $"itemType={OffItemType} freshness={OffFreshness} amount={OffAmount} " +
                      $"stackCount={OffStackCount} reqSize={ReqSize} tempSize={TempSize}");
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

    static int RemainingFor(IntPtr item)
    {
        ToBuy.TryGetValue(item, out int toBuy);
        Bought.TryGetValue(item, out int bought);
        return toBuy - bought;
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

    // ---------- the hook: collect ----------

    static void BuyItemHook(IntPtr self, IntPtr request, float discount, IntPtr tempItem, IntPtr method)
    {
        try
        {
            if (CurrentGhost == IntPtr.Zero || request == IntPtr.Zero || tempItem == IntPtr.Zero ||
                *(IntPtr*)(request + OffCustomer) != CurrentGhost)
            {
                Original(self, request, discount, tempItem, method);
                return;
            }

            IntPtr item = *(IntPtr*)(request + OffItemType);
            int* stack  = (int*)(tempItem + OffStackCount);
            if (!ToBuy.ContainsKey(item)) ToBuy[item] = *stack;

            Defer(self, request, discount, tempItem, method, item, *(int*)(request + OffAmount));
            // the purchase happens in FlushAll, in the chosen order
        }
        catch (Exception e)
        {
            L.LogError($"BuyItemHook: {e.Message}");
            try { Original(self, request, discount, tempItem, method); } catch { }
        }
    }

    static void Defer(IntPtr vendor, IntPtr request, float discount, IntPtr tempItem, IntPtr method,
        IntPtr item, int amount)
    {
        var p = new Pending
        {
            Vendor = vendor, Method = method, Discount = discount,
            Amount = amount, Seq = Seq++,
            Req = Marshal.AllocHGlobal(ReqSize),
            Temp = Marshal.AllocHGlobal(TempSize)
        };
        Buffer.MemoryCopy((void*)request, (void*)p.Req, ReqSize, ReqSize);
        Buffer.MemoryCopy((void*)tempItem, (void*)p.Temp, TempSize, TempSize);

        var v = new Vendor(vendor);
        var it = new ItemType(item);

        try { p.Price = v.GetItemBuyCost(it, *(FoodFreshness*)(request + OffFreshness)); }
        catch { p.Price = int.MaxValue; }

        try { p.Stock = v.container?.GetItemCount(it) ?? 0; }
        catch { p.Stock = 0; }

        p.Hops = HopsTo(vendor);

        p.Score = p.Price
                * (1.0 + DistanceWeight.Value * p.Hops)
                * (1.0 + ScarcityWeight.Value / Math.Max(p.Stock, 1));

        if (!PendingByItem.TryGetValue(item, out var list))
        {
            list = new SCG.List<Pending>();
            PendingByItem[item] = list;
            PendingOrder.Add(item);
        }
        list.Add(p);
    }

    // ---------- the flush: sort and buy ----------

    static SCG.List<Pending> Order(SCG.List<Pending> list)
    {
        switch (VendorSort.Value)
        {
            case SortMode.Cheapest:
                return list.OrderBy(p => p.Price).ThenByDescending(p => p.Stock).ThenBy(p => p.Seq).ToList();
            case SortMode.Local:
                return list.OrderBy(p => p.Hops).ThenByDescending(p => p.Stock).ThenBy(p => p.Price).ThenBy(p => p.Seq).ToList();
            case SortMode.Balanced:
                return list.OrderBy(p => p.Score).ThenByDescending(p => p.Stock).ThenBy(p => p.Seq).ToList();
            default: // Vanilla
                return list.OrderByDescending(p => p.Stock).ThenBy(p => p.Seq).ToList();
        }
    }

    static bool FlushAll()
    {
        bool boughtAny = false;
        try
        {
            foreach (IntPtr item in PendingOrder)
            {
                var sorted = Order(PendingByItem[item]);

                foreach (var p in sorted)
                {
                    int remaining = RemainingFor(item);
                    if (remaining <= 0)
                    {
                        if (Verbose.Value)
                            L.LogInfo($"Skip {NameOf(item)} at {NameOf(p.Vendor)} " +
                                      $"(price {p.Price}, stock {p.Stock}, hops {p.Hops}): order filled");
                        continue;
                    }

                    int n = Math.Min(p.Amount, remaining);

                    *(int*)(p.Req + OffAmount) = n;
                    *(int*)(p.Temp + OffStackCount) = remaining;

                    if (Verbose.Value)
                        L.LogInfo($"Buy {NameOf(item)} x{n} at {NameOf(p.Vendor)} " +
                                  $"(price {p.Price}, stock {p.Stock}, hops {p.Hops}, score {p.Score:0}) " +
                                  $"[{VendorSort.Value}, {sorted.Count} vendors]");

                    int before = RemainingFor(item);
                    Original(p.Vendor, p.Req, p.Discount, p.Temp, p.Method);

                    if (RemainingFor(item) < before)
                    {
                        boughtAny = true;
                    }
                    else
                    {
                        // TryMakePurchase refused, almost always because the venue is out of money.
                        // A failed BuyItem puts newly created items into the vendor's stock, so stop
                        // here, the same point where the game's own loop breaks on low cash.
                        if (Verbose.Value)
                            L.LogInfo($"Purchase failed for {NameOf(item)} at {NameOf(p.Vendor)} " +
                                      "(likely out of money). Stopping this recipe's purchases.");
                        return boughtAny;
                    }
                }
            }
        }
        catch (Exception e)
        {
            L.LogError($"FlushAll: {e.Message}");
        }
        return boughtAny;
    }

    static void FreePending()
    {
        foreach (var list in PendingByItem.Values)
            foreach (var p in list)
            {
                if (p.Req != IntPtr.Zero) Marshal.FreeHGlobal(p.Req);
                if (p.Temp != IntPtr.Zero) Marshal.FreeHGlobal(p.Temp);
            }
        PendingByItem.Clear();
        PendingOrder.Clear();
        Seq = 0;
    }

    // ---------- recipe context (Harmony, safe signatures) ----------

    static void Reset()
    {
        FreePending();
        CurrentGhost = IntPtr.Zero;
        CurrentVenueLoc = null;
        ToBuy.Clear();
        Bought.Clear();
    }

    [HarmonyPatch(typeof(VenueAreaGhost), nameof(VenueAreaGhost.TryPurchaseIngredients))]
    [HarmonyPrefix]
    static void RecipePre(VenueAreaGhost __instance)
    {
        Reset();
        if (__instance == null) return;
        CurrentGhost = __instance.Pointer;
        try { CurrentVenueLoc = __instance.Venue?.Location; }
        catch { CurrentVenueLoc = null; }
    }

    [HarmonyPatch(typeof(VenueAreaGhost), nameof(VenueAreaGhost.TryPurchaseIngredients))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    static void RecipePost(ref bool __result)
    {
        try
        {
            if (CurrentGhost != IntPtr.Zero && PendingOrder.Count > 0 && FlushAll())
                __result = true;
        }
        finally { Reset(); }
    }

    [HarmonyPatch(typeof(VenueAreaGhost), nameof(VenueAreaGhost.TryMakePurchase))]
    [HarmonyPostfix]
    static void PurchasePost(VenueAreaGhost __instance, ItemType stackType,
        IL2List boughtInstances, bool __result)
    {
        try
        {
            if (!__result || CurrentGhost == IntPtr.Zero || stackType == null) return;
            if (__instance == null || __instance.Pointer != CurrentGhost) return;

            IntPtr key = stackType.Pointer;
            int n = boughtInstances?.Count ?? 0;
            Bought[key] = (Bought.TryGetValue(key, out int b) ? b : 0) + n;
        }
        catch (Exception e) { L.LogError($"PurchasePost: {e.Message}"); }
    }
}