using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.Locale.UI;
using NivalisModKit;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisStaffOrder;

// Nivalis Staff Order: up and down arrows on each row of a venue's staff list, to put your staff in the order you like
// (cooks together, the manager on top). Display only: the order of the list doesn't change who works when or what they
// do first (shifts, roles and task priority decide that). The order is kept per venue with the save, and new hires go
// to the bottom.
[BepInPlugin(Guid, "Nivalis Staff Order", "1.0.0")]
[BepInDependency(ModKit.Guid, ">=0.2.0")]   // SaveData
public class Plugin : BasePlugin
{
    internal const string Guid = "bgasm.nivalis.stafforder";
    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        new Harmony(Guid).PatchAll(typeof(Patches));
    }
}

[HarmonyPatch]
static class Patches
{
    // The staff tab draws one row per person in the venue's staff list, in list order: put the list in the saved order
    // first, then add the arrows to the rows it drew.
    [HarmonyPatch(typeof(VenueStaffOverviewTab), nameof(VenueStaffOverviewTab.RefreshStaffList), new[] { typeof(Venue) }), HarmonyPrefix]
    static void BeforeRefresh(Venue venue)
    {
        try { StaffOrder.Apply(venue); }
        catch (Exception e) { Plugin.L.LogWarning($"Staff Order: {e.Message}"); }
    }

    [HarmonyPatch(typeof(VenueStaffOverviewTab), nameof(VenueStaffOverviewTab.RefreshStaffList), new[] { typeof(Venue) }), HarmonyPostfix]
    static void AfterRefresh(VenueStaffOverviewTab __instance, Venue venue)
    {
        try { Arrows.Decorate(__instance, venue); }
        catch (Exception e) { Plugin.L.LogWarning($"Staff Order: {e.Message}"); }
    }
}

// The order: a list of person GUIDs per venue, in the kit's save data.
static class StaffOrder
{
    static ModSaveData Store => SaveData.For(Plugin.Guid);

    static string KeyOf(Venue venue)
    {
        try { return "order:" + (venue?.Guid ?? venue?.name ?? "venue"); } catch { return "order:venue"; }
    }

    internal static Il2CppSystem.Collections.Generic.List<Person> ListOf(Venue venue)
    {
        try { return venue?.RuntimeData?.staff?.backingList; } catch { return null; }
    }

    internal static string GuidOf(Person p)
    {
        try { return p?.Guid; } catch { return null; }
    }

    // Saved people first, in the saved order; anyone not in it (new hires) after them, in the game's order.
    internal static void Apply(Venue venue)
    {
        var list = ListOf(venue);
        var saved = Store.Get<List<string>>(KeyOf(venue));
        if (list == null || list.Count < 2 || saved == null || saved.Count == 0) return;
        var people = new List<Person>();
        for (int i = 0; i < list.Count; i++) people.Add(list[i]);
        var rank = new Dictionary<string, int>();
        for (int i = 0; i < saved.Count; i++) rank[saved[i]] = i;
        var sorted = people.Select((p, i) => (p, i))
            .OrderBy(t => GuidOf(t.p) is { } g && rank.TryGetValue(g, out var r) ? r : saved.Count + t.i)
            .Select(t => t.p).ToList();
        if (sorted.SequenceEqual(people)) return;
        for (int i = 0; i < sorted.Count; i++) list[i] = sorted[i];
    }

    // Moves one person up (-1) or down (+1) and saves the whole order.
    internal static bool Move(Venue venue, string guid, int step)
    {
        var list = ListOf(venue);
        if (list == null) return false;
        int from = -1;
        for (int i = 0; i < list.Count; i++) if (GuidOf(list[i]) == guid) { from = i; break; }
        int to = from + step;
        if (from < 0 || to < 0 || to >= list.Count) return false;
        var moved = list[from];
        list[from] = list[to];
        list[to] = moved;
        var order = new List<string>();
        for (int i = 0; i < list.Count; i++) if (GuidOf(list[i]) is { } g) order.Add(g);
        Store.Set(KeyOf(venue), order);
        return true;
    }
}

// The arrows: a small up/down pair on each person's row, left of the game's fire button. Rows are pooled and reused,
// so each refresh re-points the arrows at whoever the row now shows.
static class Arrows
{
    const string Name = "StaffOrderArrows";
    static readonly Color Gold = new Color32(0xcc, 0xa2, 0x69, 0xff);
    static Sprite up, down;
    static VenueStaffOverviewTab tab;
    static Venue venue;
    static readonly Dictionary<int, (string guid, int index, int count)> rows = new();   // arrows' instance id -> who

    internal static void Decorate(VenueStaffOverviewTab staffTab, Venue forVenue)
    {
        tab = staffTab;
        venue = forVenue;
        var list = StaffOrder.ListOf(forVenue);
        var parent = staffTab?.staffList?._itemDisplayParent;
        if (list == null || parent == null) return;

        // Who is on which row: rows show the person's name; match it to the list.
        var byName = new Dictionary<string, (Person p, int i)>();
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            string n = null;
            try { n = p.DisplayedName ?? p.Name; } catch { }
            if (!string.IsNullOrEmpty(n) && !byName.ContainsKey(n)) byName[n] = (p, i);
        }

        for (int c = 0; c < parent.childCount; c++)
        {
            var row = parent.GetChild(c);
            var panel = row.Find("WorkerPanel");
            var arrows = panel?.Find(Name);
            var label = panel?.Find("LeftSide/NameAndHappiness/StaffName")?.GetComponent<TMP_Text>();
            bool person = row.gameObject.activeSelf && panel != null && panel.gameObject.activeSelf && label != null
                          && byName.TryGetValue(label.text ?? "", out _);
            if (!person) { if (arrows != null) arrows.gameObject.SetActive(false); continue; }

            var (who, index) = byName[label.text];
            arrows ??= Build(panel);
            arrows.gameObject.SetActive(true);
            rows[arrows.gameObject.GetInstanceID()] = (StaffOrder.GuidOf(who), index, list.Count);
            arrows.Find("Up").GetComponent<Button>().interactable = index > 0;
            arrows.Find("Down").GetComponent<Button>().interactable = index < list.Count - 1;
        }
    }

    static Transform Build(Transform panel)
    {
        var box = new GameObject(Name).AddComponent<RectTransform>();
        box.SetParent(panel, false);
        box.anchorMin = box.anchorMax = new Vector2(1f, 0.5f);
        box.pivot = new Vector2(1f, 0.5f);
        box.anchoredPosition = new Vector2(-58f, 0f);   // left of the fire button (30 wide, at the right edge)
        box.sizeDelta = new Vector2(26f, 58f);
        int id = box.gameObject.GetInstanceID();
        Arrow(box, "Up", up ??= Triangle(true), 14f, () => Click(id, -1));
        Arrow(box, "Down", down ??= Triangle(false), -14f, () => Click(id, +1));
        return box;
    }

    static void Arrow(RectTransform box, string name, Sprite sprite, float y, Action onClick)
    {
        var rt = new GameObject(name).AddComponent<RectTransform>();
        rt.SetParent(box, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, y);
        rt.sizeDelta = new Vector2(26f, 24f);
        var image = rt.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = Color.white;
        var button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.normalColor = Gold;
        colors.highlightedColor = new Color(1f, 0.86f, 0.55f, 1f);
        colors.pressedColor = new Color(0.7f, 0.55f, 0.35f, 1f);
        colors.selectedColor = Gold;
        colors.disabledColor = new Color(0.45f, 0.42f, 0.38f, 0.35f);
        button.colors = colors;
        button.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick()));
    }

    static void Click(int id, int step)
    {
        try
        {
            if (!rows.TryGetValue(id, out var row) || row.guid == null || venue == null) return;
            if (StaffOrder.Move(venue, row.guid, step)) tab?.RefreshStaffList(venue);
        }
        catch (Exception e) { Plugin.L.LogWarning($"Staff Order: {e.Message}"); }
    }

    // A white triangle with soft edges, pointing up or down; the button tints it.
    static Sprite Triangle(bool pointsUp)
    {
        const int w = 26, h = 24;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // Triangle with its tip at the top (or bottom) centre and its base 6 px from the other edge.
                float fy = pointsUp ? y + 0.5f : h - (y + 0.5f);   // 0 at the base side, h at the tip side
                float top = h - 5f, bottom = 5f;
                float t = (fy - bottom) / (top - bottom);            // 0 at the base, 1 at the tip
                float half = (1f - t) * 9f;                          // half-width: 9 px at the base, 0 at the tip
                float d = Mathf.Min(half - Mathf.Abs(x + 0.5f - w / 2f), Mathf.Min(fy - bottom, top - fy));
                px[y * w + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(d + 0.5f) * 255));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var s = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        s.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return s;
    }
}
