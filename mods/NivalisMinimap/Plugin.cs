using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Nivalis;
using NivalisModKit;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NivalisMinimap;

// A minimap for Nivalis Nights. Each district's map is made on the player's machine the first time they visit (a
// satellite photo from above, or a drawing of its walkable ground), cached, and shown in a round HUD corner map.
// Built on the kit's world APIs (0.4): Navigation, Photo, Player, Ui.GameHudAlpha, World.Places, Economy.Stalls,
// Venues.Entrances, Quests.Markers. Dev command "minimap": the research tools it was built with (DevTools).
public enum QuestMarkerMode { Pinned, All, Off }

[BepInPlugin(Guid, "Nivalis Minimap", "0.1.1")]
[BepInDependency(ModKit.Guid, ">=0.4.0")]   // Navigation, Photo, Player, World.Places, Quests.Markers...
public class Plugin : BasePlugin
{
    const string Guid = "bgasm.nivalis.minimap";

    internal static ManualLogSource L;
    internal static ConfigEntry<bool> Enabled, RotateWithCamera, ShowVendors, ShowPlaces, AvoidVenuePanel;
    internal static ConfigEntry<MapStyle> Style;
    internal static ConfigEntry<Corner> Corner;
    internal static ConfigEntry<float> Size, Zoom, Opacity, CutHeight, MarginX, MarginY;
    internal static ConfigEntry<string> ToggleKey, FullMapKey;
    internal static ConfigEntry<QuestMarkerMode> QuestMarkers;
    internal static UnityEngine.InputSystem.Key? ToggleKeyValue, FullMapKeyValue;

    static readonly JsonSerializerOptions json = new() { WriteIndented = true };

    public override void Load()
    {
        L = Log;

        // Settings, read live (Mods menu or the .cfg); a style change redraws the map.
        Enabled = Config.Bind("Minimap", "Enabled", true, "Show the minimap.");
        Style = Config.Bind("Minimap", "Style", MapStyle.Satellite,
            "Satellite: the district photographed from above. Cut: the same with roofs and awnings removed. " +
            "Drawn: the walkable ground drawn as a plain map.");
        Corner = Config.Bind("Minimap", "Corner", NivalisMinimap.Corner.BottomLeft, "Screen corner.");
        Size = Config.Bind("Minimap", "Size", 260f, new ConfigDescription("Diameter on a 1080p screen.",
            new AcceptableValueRange<float>(120f, 480f)));
        Zoom = Config.Bind("Minimap", "Zoom", 60f, new ConfigDescription("Metres across the map.",
            new AcceptableValueRange<float>(20f, 200f)));
        RotateWithCamera = Config.Bind("Minimap", "RotateWithCamera", true,
            "Turn the map so the camera's view is up. Off: north is always up.");
        Opacity = Config.Bind("Minimap", "Opacity", 0.95f, new ConfigDescription("How solid the map is.",
            new AcceptableValueRange<float>(0.2f, 1f)));
        CutHeight = Config.Bind("Minimap", "CutHeight", 3f, new ConfigDescription(
            "Cut style: metres above the highest walkable ground where the view is sliced off.",
            new AcceptableValueRange<float>(0.5f, 12f)));
        ShowVendors = Config.Bind("Markers", "ShowVendors", true,
            "Vendor stalls on the map: ringed by kind of shop, with something they sell inside.");
        ShowPlaces = Config.Bind("Markers", "ShowPlaces", true,
            "Places the compass points at: your apartment, shelters, venues, greenhouses, travel points, boats, trains and lifts, with its icons.");
        QuestMarkers = Config.Bind("Markers", "QuestMarkers", QuestMarkerMode.Pinned,
            "Quest objectives on the map: Pinned (the quests you track), All (others dimmed), or Off. " +
            "Off the minimap's edge they sit on the rim, pointing the way; in another district, at the way there.");
        ToggleKey = Config.Bind("Minimap", "ToggleKey", "F6",
            "Key that hides and shows the minimap (a Unity Input System key name, e.g. F6, M, Backslash). Empty: none.");
        FullMapKey = Config.Bind("Minimap", "FullMapKey", "F7",
            "Key that opens the whole district's map (M is the game's travel map). Empty: none.");
        AvoidVenuePanel = Config.Bind("Layout", "AvoidVenuePanel", true,
            "Bottom-left corner: lift the minimap above the game's venue panel while it shows (at your venues).");
        MarginX = Config.Bind("Layout", "MarginX", 28f, new ConfigDescription("Distance from the screen's side.",
            new AcceptableValueRange<float>(0f, 600f), new ModSetting { IsAdvanced = true }));
        MarginY = Config.Bind("Layout", "MarginY", 28f, new ConfigDescription("Distance from the screen's top or bottom.",
            new AcceptableValueRange<float>(0f, 600f), new ModSetting { IsAdvanced = true }));
        Style.SettingChanged += (_, _) => Hud.Rebuild();
        CutHeight.SettingChanged += (_, _) => { if (Style.Value == MapStyle.Cut) Hud.Rebuild(); };
        ToggleKey.SettingChanged += (_, _) => ReadToggleKey();
        FullMapKey.SettingChanged += (_, _) => ReadToggleKey();
        ReadToggleKey();
        ModMenu.ListSettings(Guid);
        ModMenu.AddPage(Guid, "Minimap", w =>
        {
            w.AddText("Maps are made the first time you visit a district and kept in BepInEx\\cache\\NivalisMinimap. " +
                      "Redraw this district's map if it looks out of date (after moving furniture, or at a better time of day).");
            w.AddButton("Redraw this district's map", Hud.Rebuild);
        });
        Hud.Install();
        AddComponent<MinimapBehaviour>();

        DevCommands.Register(Guid, "minimap", DevTools.Help, DevTools.Run);
    }

    static void ReadToggleKey()
    {
        ToggleKeyValue = ParseKey(ToggleKey);
        FullMapKeyValue = ParseKey(FullMapKey);
    }

    static UnityEngine.InputSystem.Key? ParseKey(ConfigEntry<string> entry)
    {
        string v = entry.Value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (Enum.TryParse(v, true, out UnityEngine.InputSystem.Key key)) return key;
        L.LogWarning($"Minimap: unknown {entry.Definition.Key} '{v}'");
        return null;
    }
}
