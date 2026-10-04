using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.Economy;
using Nivalis.InventorySystem;
using Nivalis.Apartment;
using Nivalis.Navigation;
using Nivalis.UI;
using NivalisModKit;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisMinimap;

// Vendor markers: a round badge per vendor stall in the district, ringed in a colour for the kind of shop, holding the
// icon of something it sells (the game's own item sprite, used at runtime). Stalls are found by their VendorInteraction
// component. They spawn as the district fills in, so scans back off (1, 2, 4, 8 s...) and stop once two in a row find
// the same stalls; vendors then stay until the next scene (curfew is a scene change too). Placed every frame.
//
// Places: homes (the player's apartment, shelters: free apartments), venues, greenhouses and ways to travel (travel
// points, boats, trains, lifts), with the compass's own icon and colour. Two sources, both scanned including
// switched-off objects (the compass only lists what's near; the full map shows everything):
//  - scene transitions (doors, taxis, trains, boats, lifts: SceneTransitionInteractableBase) carry their compass type
//    and a portal key naming where they go, so travel badges say "Train > Docks";
//  - CompassMarker components (apartment doors).
// Apartments only count when they're shelters (free apartments) or the player owns or rents them; every apartment
// door has a marker, rented or not.
//
// The player's venues: the compass doesn't point at them, so they're found like stalls: the venue sign at each entrance
// (VenueSignInteraction) whose venue is one the player owns (the kit's Venues.PlayerOwned). Venues the player could
// buy or rent (acquirable, not theirs) get a dimmed badge, with the prices on the full map's hover.
//
// Quests: the compass's quest markers (its live list: the game already moves a quest's marker onto the portal towards an
// objective in another district). Pinned quests only by default (QuestMarkers setting: Pinned, All, Off). Read once a
// second; on the minimap a quest off the edge sits on the rim, pointing the way.
internal static class Markers
{
    internal sealed class Marker
    {
        public VendorInteraction Stall;
        public Transform Target;              // places: follow this
        public NavigationMarkerType Type;
        public IntPtr Apartment;              // homes: their apartment, to show each once
        public RectTransform Badge;
        public Vector3 Position;
        public string Name, Kind, Stock;
        public string Detail;                 // second line on the full map's hover (for-sale venues: prices)
        public bool Dim;
        public IntPtr Quest;                  // quests: their quest, to keep one badge per quest and target
        public Color Colour;
        public Sprite Icon;
    }

    // The district's vendor stalls and places, for the full map.
    internal static IReadOnlyList<Marker> All => Every().Where(Shown).ToList();

    static readonly List<Marker> places = new();
    static readonly List<Marker> venues = new();
    static readonly List<Marker> quests = new();
    static float questsAt;

    static IEnumerable<Marker> Every() => markers.Concat(places).Concat(venues).Concat(quests);

    static bool Shown(Marker m) =>
        m.Quest != IntPtr.Zero ? Plugin.QuestMarkers.Value != QuestMarkerMode.Off :
        !UnderQuest(m) && (m.Stall != null ? Plugin.ShowVendors.Value : Plugin.ShowPlaces.Value);

    // A place or stall with a quest badge on it (a cab towards an objective): the quest badge stands in for it.
    static bool UnderQuest(Marker m) =>
        m.Target != null && quests.Exists(q => q.Target != null && q.Target.Pointer == m.Target.Pointer) ||
        m.Stall != null && quests.Exists(q => q.Target != null && (q.Target.position - m.Position).sqrMagnitude < 0.25f);

    // The compass's marker types shown as places, and what to call them.
    static readonly Dictionary<NavigationMarkerType, string> PlaceNames = new()
    {
        [NavigationMarkerType.Apartment] = "Apartment",
        [NavigationMarkerType.ApartmentCurfew] = "Apartment",
        [NavigationMarkerType.Shelter] = "Shelter",
        [NavigationMarkerType.ShelterCurfew] = "Shelter",
        [NavigationMarkerType.Venue] = "Venue",
        [NavigationMarkerType.Greenhouse] = "Greenhouse",
        [NavigationMarkerType.TravelPoint] = "Travel point",
        [NavigationMarkerType.Boat] = "Boat",
        [NavigationMarkerType.Train] = "Train",
        [NavigationMarkerType.Lift] = "Lift",
        // Not shown here: Quest (quests come later) and Trader (vendors have their own badges).
    };

    static RectTransform layer, questLayer;   // quests draw over every other badge
    static readonly List<Marker> markers = new();
    static readonly Dictionary<IntPtr, Sprite> iconCache = new();
    static float scanAt, scanGap;
    static int lastCount = -1;
    static bool settled;

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
        var go = new GameObject("Vendors");
        go.transform.SetParent(mask, false);
        layer = go.AddComponent<RectTransform>();
        layer.anchorMin = layer.anchorMax = layer.pivot = new Vector2(0.5f, 0.5f);
        layer.sizeDelta = Vector2.zero;
        var q = new GameObject("Quests");
        q.transform.SetParent(mask, false);
        questLayer = q.AddComponent<RectTransform>();
        questLayer.anchorMin = questLayer.anchorMax = questLayer.pivot = new Vector2(0.5f, 0.5f);
        questLayer.sizeDelta = Vector2.zero;
    }

    // New scene: forget the old stalls and start scanning again.
    internal static void Clear()
    {
        foreach (var m in Every()) if (m.Badge != null) UnityEngine.Object.Destroy(m.Badge.gameObject);
        markers.Clear();
        places.Clear();
        venues.Clear();
        quests.Clear();
        questsAt = 0f;

        scanAt = 0f;
        scanGap = 1f;
        lastCount = -1;
        settled = false;
    }

    // Each frame, minimap shown or not (the full map uses the stalls too): scan until the district's stalls settle.
    internal static void ScanStep()
    {
        if (layer != null && Time.unscaledTime >= questsAt)
        {
            questsAt = Time.unscaledTime + 1f;
            ReadQuests();
        }

        if (layer != null && !settled && Time.unscaledTime >= scanAt)
        {
            Scan();
            // Settled: the same stalls twice running (with some found), or nothing after half a minute of looking.
            settled = (markers.Count == lastCount && markers.Count > 0) || scanGap >= 32f;
            lastCount = markers.Count;
            scanAt = Time.unscaledTime + scanGap;
            scanGap *= 2f;
            if (settled)
                Plugin.L.LogInfo($"Minimap: {markers.Count} vendor stall(s), {venues.Count(v => !v.Dim)} of your venue(s), {venues.Count(v => v.Dim)} for sale, places: " +
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
            if (!inside && m.Quest != IntPtr.Zero)
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

    // Every place in the scene, switched off or not: scene transitions and compass markers of the place types.
    static void ScanPlaces()
    {
        places.RemoveAll(m =>
        {
            bool gone = m.Target == null;
            if (gone && m.Badge != null) UnityEngine.Object.Destroy(m.Badge.gameObject);
            return gone;
        });
        var seenApartments = new HashSet<IntPtr>(places.Where(m => m.Apartment != IntPtr.Zero).Select(m => m.Apartment));

        foreach (var portal in SceneObjects<SceneTransitionInteractableBase>())
        {
            NavigationMarkerType type;
            PortalKey key = null;
            try { type = portal.compassMarkerType; key = portal.keyTo; } catch { continue; }
            if (!PlaceNames.ContainsKey(type)) continue;
            string name;
            Apartment apartment = null;
            try { apartment = key?.apartment; } catch { }
            if (IsHome(type) || apartment != null)
            {
                if (!HomeName(apartment, out name) || !seenApartments.Add(apartment.Pointer)) continue;
            }
            else name = WithDestination(PlaceNames[type], key);
            AddPlace(portal.transform, type, name, apartment);
        }

        foreach (var marker in SceneObjects<CompassMarker>())
        {
            NavigationMarkerType type;
            try { type = marker.type; } catch { continue; }
            if (!PlaceNames.ContainsKey(type)) continue;
            string name = PlaceNames[type];
            Apartment apartment = null;
            if (IsHome(type))
            {
                apartment = ApartmentOf(marker.transform);
                if (!HomeName(apartment, out name) || !seenApartments.Add(apartment.Pointer)) continue;
            }
            AddPlace(marker.transform, type, name, apartment);
        }
    }

    static void AddPlace(Transform target, NavigationMarkerType type, string name, Apartment apartment)
    {
        if (places.Exists(m => m.Target != null && m.Target.Pointer == target.Pointer)) return;
        var (icon, colour) = PipLook(type);
        var m = new Marker
        {
            Target = target, Type = type, Position = target.position, Name = name, Kind = type.ToString(),
            Colour = colour, Icon = icon, Apartment = apartment != null ? apartment.Pointer : IntPtr.Zero,
        };
        m.Badge = MakeBadge(m, layer);
        places.Add(m);
    }

    static bool IsHome(NavigationMarkerType t) =>
        t is NavigationMarkerType.Apartment or NavigationMarkerType.ApartmentCurfew or
             NavigationMarkerType.Shelter or NavigationMarkerType.ShelterCurfew;

    // A home worth showing: a shelter, or an apartment the player owns or rents.
    static bool HomeName(Apartment apartment, out string name)
    {
        name = null;
        if (apartment == null) return false;
        try
        {
            if (apartment.shelter) { name = "Shelter"; return true; }
            string id = apartment.Guid;
            if (Nivalis.Singleton<PlayerManager>.InstanceExist(out var pm) && pm.LocalPlayer?._ownedProperties != null &&
                pm.LocalPlayer._ownedProperties.Contains(id)) { name = "Your apartment"; return true; }
            if (Nivalis.Singleton<RentManager>.InstanceExist(out var rm) && rm.rents != null && rm.rents.ContainsKey(id))
            { name = "Your apartment"; return true; }
        }
        catch { }
        return false;
    }

    // The apartment an apartment door's marker belongs to: an ApartmentKeyMarkerController above it.
    static Apartment ApartmentOf(Transform t)
    {
        for (var p = t; p != null; p = p.parent)
        {
            var controller = p.GetComponent<ApartmentKeyMarkerController>();
            if (controller != null)
            {
                try { return controller.apartment; } catch { return null; }
            }
        }
        return null;
    }

    // "Train" + its portal key's destination: "Train > Docks" (unless it stays in this district).
    static string WithDestination(string label, PortalKey key)
    {
        try
        {
            var where = key?.targetLocation;
            if (where == null) return label;
            string name = World.NameOf(where);
            return string.IsNullOrEmpty(name) ? label : $"{label} > {name}";
        }
        catch { return label; }
    }

    // Components of a type in the loaded scenes, including switched-off ones (Resources.FindObjectsOfTypeAll also
    // returns prefabs and other assets, which belong to no scene).
    static IEnumerable<T> SceneObjects<T>() where T : Component
    {
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
        {
            var c = o.TryCast<T>();
            if (c == null) continue;
            var scene = c.gameObject.scene;
            if (scene.IsValid() && scene.isLoaded) yield return c;
        }
    }

    // The compass's icon and colour for a marker type.
    static (Sprite icon, Color colour) PipLook(NavigationMarkerType type)
    {
        try
        {
            if (Nivalis.Singleton<NavigationManager>.InstanceExist(out var nav) && nav?.navigationDisplayDictionary != null &&
                nav.navigationDisplayDictionary.TryGetValue(type, out var pip) && pip != null)
                return (pip.icon, pip.color.a > 0f ? pip.color : DefaultKind);
        }
        catch { }
        return (null, DefaultKind);
    }

    // A property's name as the game shows it (localized); else its asset name tidied: "_venue_noodlebar" -> "Noodlebar".
    static string PropertyName(Nivalis.GhostSystem.CustomerLoop.BaseProperty property, string assetName)
    {
        try
        {
            string shown = property?.GetName();
            if (!string.IsNullOrWhiteSpace(shown)) return shown.Trim();
        }
        catch { }
        if (string.IsNullOrEmpty(assetName)) return null;
        string s = System.Text.RegularExpressions.Regex.Replace(assetName, "^_*venue_*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ").Trim();
        return s.Length == 0 ? assetName : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    // The compass's quest markers, filtered to pinned quests (or all, or none).
    static void ReadQuests()
    {
        var mode = Plugin.QuestMarkers.Value;
        var live = new List<(Transform target, Quest quest, bool pinned, string name)>();
        if (mode != QuestMarkerMode.Off)
        {
            try
            {
                var info = new Dictionary<IntPtr, (bool pinned, string name)>();
                foreach (var rq in Quests.Active)
                    {
                        if (rq?.Quest == null) continue;
                        string title = null;
                        try { title = rq.Quest.Title; } catch { }
                        info[rq.Quest.Pointer] = (rq.Pinned, $"#{rq.QuestNumber} {title}".Trim());
                    }
                if (Nivalis.Singleton<NavigationManager>.InstanceExist(out var nav) && nav?._markers != null)
                    foreach (var d in nav._markers)
                    {
                        if (d == null || d.Type != NavigationMarkerType.Quest || d.Target == null || d.Quest == null) continue;
                        info.TryGetValue(d.Quest.Pointer, out var q);
                        if (mode == QuestMarkerMode.Pinned && !q.pinned) continue;
                        live.Add((d.Target, d.Quest, q.pinned, q.name ?? "Quest"));
                    }
            }
            catch (Exception e) { Plugin.L.LogWarning($"Minimap: reading quests: {e.Message}"); }
        }

        quests.RemoveAll(m =>
        {
            int i = live.FindIndex(l => m.Target != null && l.target.Pointer == m.Target.Pointer && l.quest.Pointer == m.Quest);
            bool gone = i < 0 || live[i].pinned != !m.Dim;   // gone, or pinned-ness changed (redrawn below)
            if (gone && m.Badge != null) UnityEngine.Object.Destroy(m.Badge.gameObject);
            return gone;
        });
        foreach (var (target, quest, pinned, name) in live)
        {
            if (quests.Exists(m => m.Target != null && m.Target.Pointer == target.Pointer && m.Quest == quest.Pointer)) continue;
            var (icon, colour) = PipLook(NavigationMarkerType.Quest);
            var m = new Marker
            {
                Target = target, Type = NavigationMarkerType.Quest, Position = target.position, Name = name,
                Kind = "Quest", Colour = colour, Icon = icon, Quest = quest.Pointer, Dim = !pinned,
            };
            m.Badge = MakeBadge(m, questLayer);
            quests.Add(m);
        }
    }

    // The player's venues in this scene: signs whose venue they own.
    static void ScanVenues()
    {
        var owned = new Dictionary<IntPtr, string>();
        try
        {
            foreach (var area in Venues.PlayerOwned)
                if (area?.Venue != null) owned[area.Venue.Pointer] = PropertyName(area.Venue, Venues.NameOf(area));
        }
        catch { }
        venues.RemoveAll(m =>
        {
            bool gone = m.Target == null;
            if (gone && m.Badge != null) UnityEngine.Object.Destroy(m.Badge.gameObject);
            return gone;
        });
        foreach (var sign in SceneObjects<VenueSignInteraction>())
        {
            var venue = sign.venue;
            if (venue == null) continue;
            if (venues.Exists(m => m.Target != null && m.Target.Pointer == sign.transform.Pointer)) continue;
            bool mine = owned.TryGetValue(venue.Pointer, out var name);
            bool forSale = false;
            string detail = null;
            if (!mine)
            {
                try { forSale = venue.IsAcquireable; } catch { }
                if (!forSale) continue;
                name = PropertyName(venue, venue.name);
                try { detail = $"For sale · buy {venue.BuyCost:N0} · rent {venue.RentCost:N0}"; } catch { detail = "For sale"; }
            }
            var (icon, colour) = PipLook(NavigationMarkerType.Venue);
            if (icon == null) { try { icon = sign.venue.iconSquare; } catch { } }   // the venue's own icon
            var m = new Marker
            {
                Target = sign.transform, Type = NavigationMarkerType.Venue, Position = sign.transform.position,
                Name = string.IsNullOrEmpty(name) ? (mine ? "Your venue" : "Venue") : name, Kind = "Venue",
                Colour = colour, Icon = icon, Detail = detail, Dim = forSale,
            };
            m.Badge = MakeBadge(m, layer);
            venues.Add(m);
        }
    }

    static void Scan()
    {
        ScanVenues();
        ScanPlaces();
        markers.RemoveAll(m =>
        {
            bool gone = m.Stall == null;
            if (gone && m.Badge != null) UnityEngine.Object.Destroy(m.Badge.gameObject);
            return gone;
        });
        foreach (var stall in SceneObjects<VendorInteraction>())
        {
            var existing = markers.FirstOrDefault(m => m.Stall != null && m.Stall.Pointer == stall.Pointer);
            if (existing != null) { existing.Position = stall.transform.position; continue; }
            Vendor vendor = null;
            try { vendor = stall.definition; } catch { }
            if (vendor == null) continue;
            var marker = new Marker { Stall = stall, Position = stall.transform.position };
            Describe(marker, vendor);
            marker.Badge = MakeBadge(marker, layer);
            markers.Add(marker);
        }
    }

    static void Describe(Marker m, Vendor vendor)
    {
        m.Name = Economy.NameOf(vendor) ?? "Vendor";
        m.Kind = "";
        try { m.Kind = ((UnityEngine.Object)vendor.type)?.name ?? ""; } catch { }
        m.Colour = DefaultKind;
        foreach (var (word, c) in Kinds)
            if (m.Kind.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) { m.Colour = c; break; }
        m.Icon = IconFor(vendor);
        m.Stock = StockOf(vendor);
    }

    // What the vendor stocks, from its offers: tag names ("Herbs") and single items, the first few.
    static string StockOf(Vendor vendor)
    {
        var names = new List<string>();
        try
        {
            var offers = vendor.offerredItems;
            if (offers != null)
                foreach (var offer in offers)
                {
                    string n = null;
                    if (offer?.itemType != null) n = Items.NameOf(offer.itemType);
                    else if (offer?.tag != null) n = ((UnityEngine.Object)offer.tag).name;
                    if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                }
        }
        catch { }
        if (names.Count == 0) return "";
        return string.Join(" · ", names.Take(5)) + (names.Count > 5 ? " ..." : "");
    }

    // A badge: the ring in the shop's colour, a dark centre, and the item icon. Sized by its parent's layout.
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
        var icon = m.Icon;
        if (icon != null)
        {
            var iconRect = Rect("Icon", inner);
            Stretch(iconRect, 0.12f);
            var iconImage = iconRect.gameObject.AddComponent<Image>();
            iconImage.sprite = icon;
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
