using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using Il2CppInterop.Runtime;
using NivalisModKit;
using UnityEngine;

namespace NivalisMinimap;

// The dev command "minimap": the research tools the minimap was built with. Files go to BepInEx\cache\NivalisMinimap.
//   minimap                      this scene's navigation mesh: size, triangles, the player on it
//   minimap state                how visible the minimap is, whose map it holds (cheap; for tests)
//   minimap refresh              redraw the HUD map
//   minimap dump [size=1024]     the mesh drawn to a PNG, the player marked
//   minimap shot [ppm=10] [cut=3] [exclude=Layer,..] [hide=name,..] [exposure=1] [knee=0.75] [lightcap=3]
//                                Photo.TopDown of the walkable area: satellite, hybrid (walkable outlined), mesh PNGs
//   minimap lights [top=30]      the scene's lights, strongest first
//   minimap renderers [top=40]   what's drawn, grouped by shader and material (names for hide=)
internal static class DevTools
{
    internal const string Help =
        "This scene's navigation mesh; `minimap state` the HUD's visibility; `minimap refresh` redraws the HUD map; `minimap dump [size=1024]` draws the mesh; " +
        "`minimap shot [ppm=10] [cut=3] [exclude=Layer,..] [hide=name,..] [exposure=1] [knee=0.75] [lightcap=3]` photographs it " +
        "from above; `minimap lights` and `minimap renderers` list lights and what's drawn. Files in BepInEx\\cache\\NivalisMinimap";

    static readonly JsonSerializerOptions json = new() { WriteIndented = true };

    static string Dir
    {
        get
        {
            string dir = Path.Combine(Paths.CachePath, "NivalisMinimap");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    static string SceneFile => string.Join("_", Scenes.Active.Split(Path.GetInvalidFileNameChars()));

    internal static object Run(CommandArgs a)
    {
        if (a.Has("state")) return Hud.State();
        if (!GameEvents.IsInGame) throw new InvalidOperationException("load a save first");
        if (a.Has("refresh")) { Hud.Rebuild(); return new { redrawing = Scenes.Active }; }
        if (a.Has("shot")) return Shot(a);
        if (a.Has("lights")) return Lights(Math.Clamp(a.GetInt("top", 30), 1, 500));
        if (a.Has("renderers")) return Renderers(Math.Clamp(a.GetInt("top", 40), 1, 500));
        return Mesh(a.Has("dump"), Math.Clamp(a.GetInt("size", 1024), 128, 4096));
    }

    static Navigation.Mesh RequireMesh() =>
        Navigation.Triangulate() ?? throw new InvalidOperationException("no navigation mesh loaded");

    static object Mesh(bool dump, int size)
    {
        var started = DateTime.UtcNow;
        var mesh = RequireMesh();
        var map = NavMap.Draw(mesh, dump ? size : 256);
        double ms = (DateTime.UtcNow - started).TotalMilliseconds;
        var player = Player.Transform;
        Vector2? pixel = player != null ? map.ToPixel(player.position) : null;
        var info = new
        {
            scene = Scenes.Active,
            vertices = mesh.Vertices.Length,
            triangles = mesh.Triangles,
            trianglesByArea = mesh.Areas.GroupBy(x => x).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            boundsMin = V(mesh.Bounds.min),
            boundsMax = V(mesh.Bounds.max),
            sizeMetres = V(mesh.Bounds.size),
            pixelsPerMetre = Math.Round(map.PixelsPerMetre, 3),
            image = new { width = map.Width, height = map.Height },
            player = player != null ? V(player.position) : null,
            playerPixel = pixel is { } pp ? new { x = Math.Round(pp.x), y = Math.Round(pp.y) } : null,
            buildMs = Math.Round(ms),
        };
        if (!dump) return info;
        if (player != null) map.Marker(player.position, Player.Heading, new Color32(235, 60, 60, 255));
        string file = Path.Combine(Dir, SceneFile + ".png");
        File.WriteAllBytes(file, map.EncodePng());
        File.WriteAllText(Path.Combine(Dir, SceneFile + ".json"), JsonSerializer.Serialize(info, json));
        return new { file, info };
    }

    static object Shot(CommandArgs a)
    {
        var mesh = RequireMesh();
        var options = new PhotoOptions
        {
            PixelsPerMetre = Math.Clamp(a.GetFloat("ppm", 10f), 1f, 40f),
            CutAt = a.Has("cut") ? mesh.Bounds.max.y + a.GetFloat("cut", 3f) : null,
            ExcludeLayers = Photo.DefaultExcludedLayers.Concat(List(a.Get("exclude"))).ToArray(),
            Hide = Photo.DefaultHidden.Concat(List(a.Get("hide"))).ToArray(),
            Exposure = Math.Clamp(a.GetFloat("exposure", 1f), 0.05f, 8f),
            Knee = Math.Clamp(a.GetFloat("knee", 0.75f), 0.1f, 1f),
            LightCap = Math.Clamp(a.GetFloat("lightcap", 3f), 0f, 100f),
        };
        var photo = Photo.TopDown(mesh.Bounds, options);
        var walk = NavMap.Draw(mesh, photo);
        string name = SceneFile;
        File.WriteAllBytes(Path.Combine(Dir, name + "_satellite.png"), photo.ToPng());
        File.WriteAllBytes(Path.Combine(Dir, name + "_hybrid.png"), NavMap.EncodePng(Hybrid(walk, photo.Pixels), photo.Width, photo.Height));
        File.WriteAllBytes(Path.Combine(Dir, name + "_mesh.png"), walk.EncodePng());
        return new
        {
            folder = Dir,
            images = new[] { "_satellite", "_hybrid", "_mesh" }.Select(s => name + s + ".png").ToArray(),
            image = new { width = photo.Width, height = photo.Height },
            pixelsPerMetre = Math.Round(photo.PixelsPerMetre, 3),
            roofs = options.CutAt is { } cut ? $"cut at {cut:0.00} m" : "kept",
            hiddenRenderers = photo.HiddenRenderers,
            cappedLights = photo.CappedLights,
            retaken = photo.Retaken,
            gameTime = $"{GameTime.Hour:00}:{GameTime.Minute:00}",
            renderMs = Math.Round(photo.RenderMs),
        };
    }

    // The scene's lights, strongest first (intensity x range).
    static object Lights(int top) =>
        UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>())
            .Select(o => o.TryCast<Light>())
            .Where(l => l != null && l.enabled && l.gameObject.activeInHierarchy)
            .OrderByDescending(l => l.intensity * Mathf.Max(l.range, 1f))
            .Take(top)
            .Select(l => new
            {
                name = l.gameObject.name,
                type = l.type.ToString(),
                intensity = Math.Round(l.intensity, 2),
                range = Math.Round(l.range, 1),
                position = V(l.transform.position),
            }).ToArray();

    // What's drawn, grouped by shader and material, most common first; also saved (it can outlast the bridge's wait).
    static object Renderers(int top)
    {
        var groups = new Dictionary<string, (int count, string layer, float size, string sample)>();
        foreach (var r in Scenes.Objects<Renderer>())
        {
            if (!r.enabled) continue;
            string shader = "", material = "";
            try { var m = r.sharedMaterial; material = m != null ? m.name : ""; shader = m?.shader != null ? m.shader.name : ""; } catch { }
            string key = $"{shader} | {material}";
            float size = 0f;
            try { var s = r.bounds.size; size = Mathf.Max(s.x, Mathf.Max(s.y, s.z)); } catch { }
            groups.TryGetValue(key, out var g);
            groups[key] = (g.count + 1, g.layer ?? LayerMask.LayerToName(r.gameObject.layer),
                           g.count == 0 ? size : (g.size * g.count + size) / (g.count + 1), g.sample ?? r.gameObject.name);
        }
        var list = groups.OrderByDescending(kv => kv.Value.count).Take(top).Select(kv => new
        {
            shaderAndMaterial = kv.Key,
            kv.Value.count,
            kv.Value.layer,
            averageSize = Math.Round(kv.Value.size, 2),
            example = kv.Value.sample,
        }).ToArray();
        File.WriteAllText(Path.Combine(Dir, "renderers.json"), JsonSerializer.Serialize(list, json));
        return list;
    }

    // The photo with the walkable area marked: walkable kept, the rest dimmed, a light outline between.
    static Color32[] Hybrid(NavMap map, Color32[] photo)
    {
        int w = map.Width, h = map.Height;
        var outline = new Color32(255, 236, 170, 255);
        var result = new Color32[photo.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                var p = photo[i];
                if (!map.IsWalkable(x, y)) { result[i] = new Color32((byte)(p.r * 0.6f), (byte)(p.g * 0.6f), (byte)(p.b * 0.6f), 255); continue; }
                bool edge = (x > 0 && !map.IsWalkable(x - 1, y)) || (x < w - 1 && !map.IsWalkable(x + 1, y)) ||
                            (y > 0 && !map.IsWalkable(x, y - 1)) || (y < h - 1 && !map.IsWalkable(x, y + 1));
                result[i] = edge ? Color32.Lerp(p, outline, 0.5f) : p;
            }
        return result;
    }

    static string[] List(string value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static object V(Vector3 v) => new { x = Math.Round(v.x, 2), y = Math.Round(v.y, 2), z = Math.Round(v.z, 2) };
}
