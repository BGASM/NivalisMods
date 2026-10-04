using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis.Economy;
using Nivalis.InventorySystem;
using NivalisModKit;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisMinimap;

// The map's badges, from the kit's world queries:
//  - vendor stalls (Economy.Stalls): ringed in a colour for the kind of shop, holding the icon of something they sell
//    (the game's own item sprite, used at runtime);
//  - places (World.Places): the player's apartment, shelters, greenhouses, travel points, trains, boats and lifts with
//    their destinations, in the compass's icon and colour;
//  - venues (Venues.Entrances): the player's, and dimmed ones they could buy or rent (prices on the full map's hover);
//  - quests (Quests.Markers): pinned by default; the game routes a marker onto the portal towards another district.
// Stalls, places and venues appear as a district fills in, so scans back off (1, 2, 4, 8 s...) and stop once two in a
// row find the same stalls; they then stay until the next scene (curfew is a scene change too). Quests are read once a
// second. On the minimap a quest off the edge sits on the rim, pointing the way; a quest on a place (a cab towards an
// objective) stands in for that place's badge.
internal static class Markers
{
    internal sealed class Marker
    {
        public Vendor Vendor;                 // stalls
        public Transform Target;              // follow this
        public RectTransform Badge;
        public Vector3 Position;
        public string Name;
        public string Detail;                 // second line on the full map's hover (for-sale venues: prices)
        public bool Dim;
        public bool IsQuest, IsStall;
        public IntPtr QuestKey;               // quests: one badge per quest and target
        public Color Colour;
        public Sprite Icon;
    }

    // Everything shown, for the full map.
    internal static IReadOnlyList<Marker> All => Every().Where(Shown).ToList();

    static readonly List<Marker> stalls = new(), places = new(), venues = new(), quests = new();
    static RectTransform layer, questLayer;   // quests draw over every other badge
    static readonly Dictionary<IntPtr, Sprite> iconCache = new();
    static float scanAt, scanGap, questsAt;
    static int lastCount = -1;
    static bool settled;

    static IEnumerable<Marker> Every() => stalls.Concat(places).Concat(venues).Concat(quests);

    static bool Shown(Marker m) =>
        m.IsQuest ? Plugin.QuestMarkers.Value != QuestMarkerMode.Off :
        !UnderQuest(m) && (m.IsStall ? Plugin.ShowVendors.Value : Plugin.ShowPlaces.Value);

    static bool UnderQuest(Marker m) =>
        quests.Exists(q => q.Target != null && m.Target != null &&
                           (q.Target.Pointer == m.Target.Pointer || (q.Target.position - m.Position).sqrMagnitude < 0.25f));

    // Ring colours by kind of shop (matched on the vendor type's name).
    static readonly (string word, Color colour)[] Kinds =
    {
        ("green", new Color(0.45f, 0.8f, 0.35f)), ("farmer", new Color(0.45f, 0.8f, 0.35f)),
        ("herb", new Color(0.3f, 0.7f, 0.45f)), ("seed", new Color(0.55f, 0.75f, 0.3f)), ("garden", new Color(0.55f, 0.75f, 0.3f)),
        ("butcher", new Color(0.85f, 0.3f, 0.3f)), ("fish", new Color(0.3f, 0.6f, 0.95f)),
        ("dairy", new Color(0.95f, 0.95f, 0.85f)), ("baker", new Color(0.9f, 0.7f, 0.4f)),
        ("confection", new Color(0.95f, 0.55f, 0.75f)), ("drink", new Color(0.65f, 0.4f, 0.9f)),
        ("chemistry", new Color(0.4f, 0.9f, 0.85f)), ("perfume", new Color(0.85f, 0.5f, 0.9f)),
        ("draper", new Color(0.7f, 0.6f, 0.85f)), ("art", new Color(0.95f, 0.8f, 0.3f)),
        ("hardware", new Color(0.95f, 0.6f, 0.2f)), ("appliance", new Color(0.95f, 0.6f, 0.2f)),
        ("furniture", new Color(0.8f, 0.55f, 0.35f)),
    };
    static readonly Color DefaultKind = new(0.8f, 0.8f, 0.8f);

    internal static void Build(Transform mask)
    {
        if (layer != null) return;
        layer = Layer("Badges", mask);
        questLayer = Layer("Quests", mask);
    }

    static RectTransform Layer(string name, Transform parent)
    {
        var rt = Rect(name, parent);
        rt.sizeDelta = Vector2.zero;
        return rt;
    }

    // New scene: forget everything and start scanning again.
    internal static void Clear()
    {
        foreach (var m in Every()) if (m.Badge != null) UnityEngine.Object.Destroy(m.Badge.gameObject);
        stalls.Clear(); places.Clear(); venues.Clear(); quests.Clear();
        scanAt = questsAt = 0f;
        scanGap = 1f;
        lastCount = -1;
        settled = false;
    }

    // Each frame, minimap shown or not (the full map uses the badges too).
    internal static void ScanStep()
    {
        if (layer == null) return;
        if (Time.unscaledTime >= questsAt)
        {
            questsAt = Time.unscaledTime + 1f;
            ReadQuests();
        }
        if (!settled && Time.unscaledTime >= scanAt)
        {
            ReadStalls();
            ReadPlaces();
            ReadVenues();
            // Settled: the same stalls twice running (with some found), or nothing after half a minute of looking.
            settled = (stalls.Count == lastCount && stalls.Count > 0) || scanGap >= 32f;
            lastCount = stalls.Count;
            scanAt = Time.unscaledTime + scanGap;
            scanGap *= 2f;
            if (settled)
                Plugin.L.LogInfo($"Minimap: {stalls.Count} vendor stall(s), {venues.Count(v => !v.Dim)} of your venue(s), " +
                                 $"{venues.Count(v => v.Dim)} for sale, places: " +
                                 (places.Count == 0 ? "none" : string.Join(", ", places.GroupBy(p => p.Name).Select(g => $"{g.Key} {g.Count()}"))));
        }
    }

    // Each frame: offsets from the player in metres, turned with the map, at the map's scale.
    internal static void Update(Vector3 player, float mapTurn, float pixelsPerMetre, float radius, float badgeSize)
    {
        if (layer == null) return;
        float a = mapTurn * Mathf.Deg2Rad, cos = Mathf.Cos(a), sin = Mathf.Sin(a);
        foreach (var m in Every())
        {
            if (m.Badge == null) continue;
            if (!Shown(m)) { m.Badge.gameObject.SetActive(false); continue; }
            if (m.Target != null) m.Position = m.Target.position;
            float dx = (m.Position.x - player.x) * pixelsPerMetre, dz = (m.Position.z - player.z) * pixelsPerMetre;
            var p = new Vector2(dx * cos - dz * sin, dx * sin + dz * cos);   // anticlockwise, as the map image turns
            bool inside = p.magnitude < radius + badgeSize;
            if (!inside && m.IsQuest)
            {
                p = p.normalized * (radius - badgeSize * 0.35f);   // on the rim, towards the objective
                inside = true;
            }
            m.Badge.gameObject.SetActive(inside);
            if (!inside) continue;
            m.Badge.anchoredPosition = p;
            m.Badge.sizeDelta = new Vector2(badgeSize, badgeSize);
        }
    }

    // ---------- sources ----------

    static void ReadStalls()
    {
        Prune(stalls);
        foreach (var s in Economy.Stalls())
        {
            if (stalls.Exists(m => m.Target != null && m.Target.Pointer == s.Transform.Pointer)) continue;
            var m = new Marker { Vendor = s.Vendor, Target = s.Transform, Position = s.Position, IsStall = true };
            m.Name = Economy.NameOf(s.Vendor) ?? "Vendor";
            string kind = "";
            try { kind = ((UnityEngine.Object)s.Vendor.type)?.name ?? ""; } catch { }
            m.Colour = DefaultKind;
            foreach (var (word, c) in Kinds)
                if (kind.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) { m.Colour = c; break; }
            m.Icon = IconFor(s.Vendor);
            Add(stalls, m, layer);
        }
    }

    static void ReadPlaces()
    {
        Prune(places);
        foreach (var p in World.Places())
        {
            if (p.Kind == PlaceKind.Venue) continue;   // venues come from their signs (ReadVenues)
            if (places.Exists(m => m.Target != null && m.Target.Pointer == p.Transform.Pointer)) continue;
            Add(places, new Marker
            {
                Target = p.Transform, Position = p.Position, Name = p.ToString(), Colour = p.Colour, Icon = p.Icon,
            }, layer);
        }
    }

    static void ReadVenues()
    {
        Prune(venues);
        var icon = World.CompassIcon(PlaceKind.Venue, out var colour);
        foreach (var v in Venues.Entrances())
        {
            if (!v.IsPlayers && !v.IsForSale) continue;
            if (venues.Exists(m => m.Target != null && m.Target.Pointer == v.Transform.Pointer)) continue;
            var venueIcon = icon;
            if (venueIcon == null) { try { venueIcon = v.Venue.iconSquare; } catch { } }   // the venue's own icon
            Add(venues, new Marker
            {
                Target = v.Transform, Position = v.Position, Name = v.Name ?? (v.IsPlayers ? "Your venue" : "Venue"),
                Colour = colour, Icon = venueIcon, Dim = v.IsForSale,
                Detail = v.IsForSale ? $"For sale · buy {v.BuyCost:N0} · rent {v.RentCost:N0}" : null,
            }, layer);
        }
    }

    static void ReadQuests()
    {
        var mode = Plugin.QuestMarkers.Value;
        var live = mode == QuestMarkerMode.Off ? new List<QuestMarker>() : Quests.Markers(pinnedOnly: mode == QuestMarkerMode.Pinned);
        IntPtr Key(QuestMarker q) => q.Quest.Pointer;
        quests.RemoveAll(m =>
        {
            var q = live.FirstOrDefault(l => m.Target != null && l.Target.Pointer == m.Target.Pointer && Key(l) == m.QuestKey);
            bool gone = q == null || q.Pinned != !m.Dim;   // gone, or pinned-ness changed (redrawn below)
            if (gone && m.Badge != null) UnityEngine.Object.Destroy(m.Badge.gameObject);
            return gone;
        });
        foreach (var q in live)
        {
            if (quests.Exists(m => m.Target != null && m.Target.Pointer == q.Target.Pointer && m.QuestKey == Key(q))) continue;
            Add(quests, new Marker
            {
                Target = q.Target, Position = q.Position, Name = $"#{q.Number} {q.Title}".Trim(), Colour = q.Colour,
                Icon = q.Icon, IsQuest = true, QuestKey = Key(q), Dim = !q.Pinned,
            }, questLayer);
        }
    }

    static void Add(List<Marker> list, Marker m, Transform parent)
    {
        m.Badge = MakeBadge(m, parent);
        list.Add(m);
    }

    // Drop markers whose objects are gone.
    static void Prune(List<Marker> list) => list.RemoveAll(m =>
    {
        bool gone = m.Target == null;
        if (gone && m.Badge != null) UnityEngine.Object.Destroy(m.Badge.gameObject);
        return gone;
    });

    // ---------- badges ----------

    // A badge: the ring in its colour, a dark centre, and the icon. Sized by its parent's layout.
    internal static RectTransform MakeBadge(Marker m, Transform parent)
    {
        var badge = Rect(m.Name, parent);
        if (m.Dim) badge.gameObject.AddComponent<CanvasGroup>().alpha = 0.55f;
        var ring = badge.gameObject.AddComponent<Image>();
        ring.sprite = Sprites.Circle();
        ring.color = m.Colour;
        var inner = Rect("Inner", badge);
        Stretch(inner, 0.14f);
        var innerImage = inner.gameObject.AddComponent<Image>();
        innerImage.sprite = Sprites.Circle();
        innerImage.color = new Color(0.08f, 0.09f, 0.11f, 0.95f);
        if (m.Icon != null)
        {
            var iconRect = Rect("Icon", inner);
            Stretch(iconRect, 0.12f);
            var iconImage = iconRect.gameObject.AddComponent<Image>();
            iconImage.sprite = m.Icon;
            iconImage.preserveAspect = true;
        }
        return badge;
    }

    // Something the vendor sells: its first offer that's a single item, else the first item carrying its first tag.
    static Sprite IconFor(Vendor vendor)
    {
        if (iconCache.TryGetValue(vendor.Pointer, out var cached)) return cached;
        Sprite icon = null;
        try
        {
            var offers = vendor.offerredItems;
            if (offers != null)
            {
                foreach (var offer in offers)
                    if (offer?.itemType != null && offer.itemType.icon != null) { icon = offer.itemType.icon; break; }
                if (icon == null)
                    foreach (var offer in offers)
                    {
                        var tag = offer?.tag;
                        if (tag == null) continue;
                        var item = Items.All.FirstOrDefault(i => i.icon != null && i.allowBuying && HasTag(i, tag));
                        if (item != null) { icon = item.icon; break; }
                    }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"Minimap: no icon for {Economy.NameOf(vendor)}: {e.Message}"); }
        iconCache[vendor.Pointer] = icon;
        return icon;
    }

    static bool HasTag(ItemType item, ObjectTag tag)
    {
        var tags = item.tags;
        if (tags == null) return false;
        foreach (var t in tags) if (t != null && t.Pointer == tag.Pointer) return true;
        return false;
    }

    static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        return rt;
    }

    // Inset as a fraction of the parent's size, so it scales with the badge.
    static void Stretch(RectTransform rt, float fraction)
    {
        rt.anchorMin = new Vector2(fraction, fraction);
        rt.anchorMax = new Vector2(1f - fraction, 1f - fraction);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
