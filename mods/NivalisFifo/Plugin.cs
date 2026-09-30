using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Nivalis.InventorySystem;
using SysList = System.Collections.Generic.List<Nivalis.InventorySystem.ItemInstanceData>;

namespace NivalisFifo;

[BepInPlugin("will.nivalis.fifo", "Use Oldest First", "0.1.0")]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        Harmony.CreateAndPatchAll(typeof(Plugin));
        Log.LogInfo("Use Oldest First loaded");
    }

    // Spoiled sorts to the front so it's never picked ahead of usable stock.
    static int Key(ItemInstanceData x) =>
        x.Freshness == FoodFreshness.Spoiled ? int.MaxValue : x.remainingDecayTime;

    // Pop takes from the end, so soonest-to-spoil goes last.
    static void SortForUse(ItemStack stack)
    {
        var list = stack._instanceData;
        if (list == null || list.Count < 2) return;

        var tmp = new SysList(list.Count);
        for (int i = 0; i < list.Count; i++) tmp.Add(list[i]);

        tmp.Sort((a, b) =>
        {
            int c = Key(b).CompareTo(Key(a));
            return c != 0 ? c : b.creationDay.CompareTo(a.creationDay);
        });

        list.Clear();
        foreach (var x in tmp) list.Add(x);
    }

    [HarmonyPatch(typeof(ItemStack), nameof(ItemStack.PopItemsInto))]
    [HarmonyPrefix]
    static void PopInto(ItemStack __instance) => SortForUse(__instance);

    [HarmonyPatch(typeof(ItemStack), nameof(ItemStack.PopItems))]
    [HarmonyPrefix]
    static void Pop(ItemStack __instance) => SortForUse(__instance);

    [HarmonyPatch(typeof(ItemStack), nameof(ItemStack.RemoveFromStack), new[] { typeof(int) })]
    [HarmonyPrefix]
    static void Remove(ItemStack __instance) => SortForUse(__instance);

    [HarmonyPatch(typeof(ItemStack), nameof(ItemStack.RemoveFromStackInto))]
    [HarmonyPrefix]
    static void RemoveInto(ItemStack __instance) => SortForUse(__instance);
}