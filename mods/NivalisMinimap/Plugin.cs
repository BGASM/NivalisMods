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
// Dev commands (minimap dump / shot / lights / renderers) are the research tools it was built with.
public enum QuestMarkerMode { Pinned, All, Off }

[BepInPlugin(Guid, "Nivalis Minimap", "0.1.0")]
[BepInDependency(ModKit.Guid, ">=0.3.0")]
public class Plugin : BasePlugin
{
    const string Guid = "bgasm.nivalis.minimap";

    internal static ManualLogSource L;
    internal static ConfigEntry<bool> Enabled, RotateWithCamera, ShowVendors, ShowPlaces;
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

        DevCommands.Register(Guid, "minimap",
            "This district's navigation mesh: stats; `minimap refresh` redraws the HUD map; `minimap dump [size=1024]` draws it to a PNG; " +
            "`minimap shot [ppm=10] [cut=3] [exclude=Layer,..] [hide=name,..] [exposure=1] [knee=0.75] [lightcap=3]` photographs it from above; `minimap lights [top=30]` lists lights; " +
            "`minimap renderers [top=40]` lists what's drawn, by shader and material. Files in BepInEx\\cache\\NivalisMinimap",
            Run);
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

    static object Run(CommandArgs a)
    {
        if (!GameEvents.IsInGame) throw new InvalidOperationException("load a save first");
        if (a.Has("refresh")) { Hud.Rebuild(); return new { redrawing = SceneManager.GetActiveScene().name }; }
        if (a.Has("shot"))
        {
            string shotDir = Path.Combine(Paths.CachePath, "NivalisMinimap");
            Directory.CreateDirectory(shotDir);
            string scene = string.Join("_", SceneManager.GetActiveScene().name.Split(Path.GetInvalidFileNameChars()));
            var options = new ShotOptions
            {
                PixelsPerMetre = Math.Clamp(a.GetFloat("ppm", 10f), 1f, 40f),
                CutAbove = a.Has("cut") ? a.GetFloat("cut", 3f) : float.NaN,
                ExcludeLayers = List(a.Get("exclude")),
                Hide = List(a.Get("hide")),
                Exposure = Math.Clamp(a.GetFloat("exposure", 1f), 0.05f, 8f),
                Knee = Math.Clamp(a.GetFloat("knee", 0.75f), 0.1f, 1f),
                LightCap = Math.Clamp(a.GetFloat("lightcap", 3f), 0f, 100f),
            };
            var result = Shot.Take(options, shotDir, scene);
            L.LogInfo($"Minimap: shot of {scene} saved");
            return result;
        }
        if (a.Has("lights")) return Shot.Lights(Math.Clamp(a.GetInt("top", 30), 1, 500));
        if (a.Has("renderers"))
        {
            // Also saved: on a busy district the list can take longer than the dev bridge waits for an answer.
            var list = Shot.Renderers(Math.Clamp(a.GetInt("top", 40), 1, 500));
            string listDir = Path.Combine(Paths.CachePath, "NivalisMinimap");
            Directory.CreateDirectory(listDir);
            File.WriteAllText(Path.Combine(listDir, "renderers.json"), JsonSerializer.Serialize(list, json));
            return list;
        }
        int size = Math.Clamp(a.GetInt("size", 1024), 128, 4096);
        var started = DateTime.UtcNow;
        var map = NavMap.Build(a.Has("dump") ? size : 256);
        if (map == null) throw new InvalidOperationException("no navigation mesh loaded");
        double buildMs = (DateTime.UtcNow - started).TotalMilliseconds;

        var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name).ToArray();
        var player = PlayerTransform();
        Vector2? playerPixel = player != null ? map.ToPixel(player.position) : null;

        var info = new
        {
            scenes,
            activeScene = SceneManager.GetActiveScene().name,
            vertices = map.Vertices,
            triangles = map.Triangles,
            trianglesByArea = map.TrianglesByArea.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            boundsMin = V(map.Min),
            boundsMax = V(map.Max),
            sizeMetres = V(map.Max - map.Min),
            pixelsPerMetre = Math.Round(map.PixelsPerMetre, 3),
            image = new { width = map.Width, height = map.Height },
            player = player != null ? V(player.position) : null,
            playerPixel = playerPixel is { } pp ? new { x = Math.Round(pp.x), y = Math.Round(pp.y) } : null,
            buildMs = Math.Round(buildMs),
        };
        if (!a.Has("dump")) return info;

        if (player != null) map.Marker(player.position, player.forward, new Color32(235, 60, 60, 255));
        string dir = Path.Combine(Paths.CachePath, "NivalisMinimap");
        Directory.CreateDirectory(dir);
        string name = string.Join("_", info.activeScene.Split(Path.GetInvalidFileNameChars()));
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), map.EncodePng());
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(info, json));
        L.LogInfo($"Minimap: {name}: {map.Triangles} triangles, {info.sizeMetres.x} x {info.sizeMetres.z} m -> {map.Width}x{map.Height} px in {buildMs:0} ms");
        return new { file = Path.Combine(dir, name + ".png"), info };
    }

    static Transform PlayerTransform()
    {
        try
        {
            return Singleton<PlayerManager>.InstanceExist(out var pm) ? pm.LocalPlayer?.PlayerGameObject?.transform : null;
        }
        catch { return null; }
    }

    static string[] List(string value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static Vec V(Vector3 v) => new(Math.Round(v.x, 2), Math.Round(v.y, 2), Math.Round(v.z, 2));

    sealed record Vec(double x, double y, double z);
}
