using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace NivalisMinimap;

internal sealed class ShotOptions
{
    public float PixelsPerMetre = 10f;
    public float CutAbove = float.NaN;           // metres above the highest walkable surface; NaN keeps roofs
    public string[] ExcludeLayers = Array.Empty<string>();
    public string[] Hide = Array.Empty<string>();   // renderers whose shader, material or object name contains one
    public float Exposure = 1f;
    public float Knee = 0.75f;                   // brightness where highlights start to roll off (0..1)
    public float LightCap = 3f;                  // local lights brighter than this are turned down for the shot; 0 = off
}

// "minimap shot": a satellite view of the district from an orthographic camera, framed on the navigation mesh's bounds
// at the same pixels per metre as the mesh map, so both images line up pixel for pixel and the same arithmetic maps
// world positions onto either. Rendered once into a texture, read back and saved; nothing renders per frame.
//
// Rendered in high dynamic range and tone mapped: the game's own camera tames bright lights with its post-processing,
// which this camera doesn't have, so in plain 0..1 colour they clip to white. Here values above the knee roll off
// smoothly towards white and everything below is left as rendered.
internal static class Shot
{
    // Left out of every shot: people (NPCs frozen mid-step, and the player, whom the map marks itself), traffic, and
    // floating UI.
    static readonly string[] DefaultExcluded = { "Character", "Player", "Vehicle", "UI", "WorldUI" };

    // Hidden in every shot: the game's grass, vine and bush shaders draw as black silhouettes for any camera but its own.
    static readonly string[] DefaultHidden = { "Vegetation/Procedural", "Advanced Bush" };

    internal sealed class Photo
    {
        public Color32[] Pixels;
        public List<string> ExcludedLayers;
        public int HiddenRenderers, CappedLights, PixelsAboveWhite;
        public bool WashedOutRenderDiscarded, Linear;
        public float BrightestValue;
        public double RenderMs;
    }

    // The district from above, framed on the mesh map so pixels line up: people, traffic and UI left out, vegetation
    // that draws black hidden, strong local lights turned down, highlights rolled off. Everything is put back after.
    internal static Photo TakePhoto(NavMap map, ShotOptions o)
    {
        var result = new Photo { ExcludedLayers = new List<string>() };
        int mask = ~0;
        foreach (var layerName in DefaultExcluded.Concat(o.ExcludeLayers).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer < 0) continue;
            mask &= ~(1 << layer);
            result.ExcludedLayers.Add(layerName);
        }

        var hidden = HideRenderers(DefaultHidden.Concat(o.Hide).ToArray());
        var capped = CapLights(o.LightCap);
        result.HiddenRenderers = hidden.Count;
        result.CappedLights = capped.Count;
        Color[] raw;
        var started = DateTime.UtcNow;
        try
        {
            // Now and then a whole render comes out washed out (a game-wide flash of some kind). Two renders, and the
            // darker one if they differ a lot.
            raw = Render(map, o.CutAbove, mask);
            var second = Render(map, o.CutAbove, mask);
            float a = MeanBrightness(raw), b = MeanBrightness(second);
            if (a > b * 1.5f) { raw = second; result.WashedOutRenderDiscarded = true; }
            else if (b > a * 1.5f) result.WashedOutRenderDiscarded = true;
        }
        finally
        {
            foreach (var r in hidden) if (r != null) r.enabled = true;
            foreach (var (light, intensity) in capped) if (light != null) light.intensity = intensity;
        }
        result.RenderMs = (DateTime.UtcNow - started).TotalMilliseconds;

        result.Linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        result.Pixels = new Color32[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (m > result.BrightestValue) result.BrightestValue = m;
            if (m > 1f) result.PixelsAboveWhite++;
            result.Pixels[i] = ToneMap(c, o.Exposure, o.Knee, result.Linear);
        }
        return result;
    }

    // "minimap shot": the photo, the photo with the walkable area marked, and the mesh map, as PNGs.
    internal static object Take(ShotOptions o, string dir, string name)
    {
        var map = NavMap.Build(4096, o.PixelsPerMetre);
        if (map == null) throw new InvalidOperationException("no navigation mesh loaded");
        var photo = TakePhoto(map, o);
        var hybrid = Hybrid(map, photo.Pixels);

        File.WriteAllBytes(Path.Combine(dir, name + "_satellite.png"), NavMap.EncodePng(photo.Pixels, map.Width, map.Height));
        File.WriteAllBytes(Path.Combine(dir, name + "_hybrid.png"), NavMap.EncodePng(hybrid, map.Width, map.Height));
        File.WriteAllBytes(Path.Combine(dir, name + "_mesh.png"), map.EncodePng());

        return new
        {
            folder = dir,
            images = new[] { "_satellite", "_hybrid", "_mesh" }.Select(s => name + s + ".png").ToArray(),
            image = new { width = map.Width, height = map.Height },
            pixelsPerMetre = Math.Round(map.PixelsPerMetre, 3),
            roofs = float.IsNaN(o.CutAbove) ? "kept" : $"cut at {map.Max.y + o.CutAbove:0.00} m",
            excludedLayers = photo.ExcludedLayers,
            hiddenRenderers = photo.HiddenRenderers,
            cappedLights = photo.CappedLights,
            washedOutRenderDiscarded = photo.WashedOutRenderDiscarded,
            colourSpace = photo.Linear ? "linear" : "gamma",
            exposure = o.Exposure,
            knee = o.Knee,
            brightestValue = Math.Round(photo.BrightestValue, 2),   // above 1: brighter than plain colour can hold
            pixelsAboveWhite = photo.PixelsAboveWhite,
            gameTime = $"{NivalisModKit.GameTime.Hour:00}:{NivalisModKit.GameTime.Minute:00}",
            renderMs = Math.Round(photo.RenderMs),
        };
    }

    // "minimap renderers": what's drawn in the district, grouped by shader and material, most common first, to find
    // names for hide=. Small: the group's typical size, to tell litter and leaves from buildings.
    internal static object Renderers(int top)
    {
        var groups = new Dictionary<string, (int count, string layer, float size, string sample)>();
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
        {
            var r = o.TryCast<Renderer>();
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            string shader = "", material = "";
            try { var m = r.sharedMaterial; material = m != null ? m.name : ""; shader = m?.shader != null ? m.shader.name : ""; } catch { }
            string key = $"{shader} | {material}";
            float size = 0f;
            try { var s = r.bounds.size; size = Mathf.Max(s.x, Mathf.Max(s.y, s.z)); } catch { }
            groups.TryGetValue(key, out var g);
            groups[key] = (g.count + 1, g.layer ?? LayerMask.LayerToName(r.gameObject.layer),
                           g.count == 0 ? size : (g.size * g.count + size) / (g.count + 1), g.sample ?? r.gameObject.name);
        }
        return groups.OrderByDescending(kv => kv.Value.count).Take(top).Select(kv => new
        {
            shaderAndMaterial = kv.Key,
            kv.Value.count,
            kv.Value.layer,
            averageSize = Math.Round(kv.Value.size, 2),
            example = kv.Value.sample,
        }).ToArray();
    }

    // "minimap lights": the scene's lights, strongest first (intensity x range), to see what lights up the map.
    internal static object Lights(int top) =>
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
                colour = $"#{B(l.color.r):X2}{B(l.color.g):X2}{B(l.color.b):X2}",
                position = new { x = Math.Round(l.transform.position.x, 1), y = Math.Round(l.transform.position.y, 1), z = Math.Round(l.transform.position.z, 1) },
            }).ToArray();

    // Local lights (not the sun) brighter than cap are turned down to it; returns them with their intensity to restore.
    static List<(Light, float)> CapLights(float cap)
    {
        var capped = new List<(Light, float)>();
        if (cap <= 0f) return capped;
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>()))
        {
            var l = o.TryCast<Light>();
            if (l == null || !l.enabled || l.type == LightType.Directional || l.intensity <= cap) continue;
            capped.Add((l, l.intensity));
            l.intensity = cap;
        }
        return capped;
    }

    static float MeanBrightness(Color[] pixels)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < pixels.Length; i += 16) { var c = pixels[i]; sum += c.r + c.g + c.b; n++; }
        return n == 0 ? 0f : (float)(sum / n);
    }

    static List<Renderer> HideRenderers(string[] words)
    {
        var hidden = new List<Renderer>();
        if (words.Length == 0) return hidden;
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
        {
            var r = o.TryCast<Renderer>();
            if (r == null || !r.enabled) continue;
            string names = r.gameObject.name;
            try
            {
                foreach (var m in r.sharedMaterials)
                    if (m != null) names += "|" + m.name + "|" + (m.shader != null ? m.shader.name : "");
            }
            catch { }
            if (!words.Any(w => names.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            r.enabled = false;
            hidden.Add(r);
        }
        return hidden;
    }

    // One frame from straight above, in high dynamic range. cutAbove: hide everything higher than that many metres
    // above the highest walkable surface (awnings, cables, roofs); NaN keeps everything.
    static Color[] Render(NavMap map, float cutAbove, int cullingMask)
    {
        // The area the image covers, from the mesh's lower corner (the pixel grid's origin).
        float worldW = map.Width / map.PixelsPerMetre, worldH = map.Height / map.PixelsPerMetre;
        float top = map.Max.y + 300f;
        var go = new GameObject("NivalisMinimap_ShotCamera");
        RenderTexture rt = null;
        Texture2D tex = null;
        try
        {
            go.transform.position = new Vector3(map.Min.x + worldW / 2f, top, map.Min.z + worldH / 2f);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // looking down, image up = north (+z)
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;                                     // only renders when asked
            cam.orthographic = true;
            cam.orthographicSize = worldH / 2f;
            cam.aspect = (float)map.Width / map.Height;
            cam.nearClipPlane = float.IsNaN(cutAbove) ? 0.3f : Mathf.Max(0.3f, top - (map.Max.y + cutAbove));
            cam.farClipPlane = top - map.Min.y + 50f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = cullingMask;
            cam.allowHDR = true;

            rt = new RenderTexture(map.Width, map.Height, 24, RenderTextureFormat.ARGBHalf);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;

            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            tex = new Texture2D(map.Width, map.Height, TextureFormat.RGBAHalf, false);
            tex.ReadPixels(new Rect(0, 0, map.Width, map.Height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;

            var src = tex.GetPixels();
            var pixels = new Color[src.Length];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = src[i];
            return pixels;
        }
        finally
        {
            if (tex != null) UnityEngine.Object.Destroy(tex);
            if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            UnityEngine.Object.Destroy(go);
        }
    }

    // Exposure, then a soft shoulder: brightness below the knee is unchanged; above it, it approaches white instead of
    // clipping. Applied to the brightest channel so colours keep their hue. Linear values are encoded to sRGB for the PNG.
    static Color32 ToneMap(Color c, float exposure, float knee, bool linear)
    {
        float r = c.r * exposure, g = c.g * exposure, b = c.b * exposure;
        if (linear) { r = ToSrgb(r); g = ToSrgb(g); b = ToSrgb(b); }
        float m = Mathf.Max(r, Mathf.Max(g, b));
        if (m > knee && m > 0f)
        {
            float over = m - knee, room = 1f - knee;
            float shoulder = knee + room * (1f - Mathf.Exp(-over / Mathf.Max(room, 1e-4f)));
            float s = shoulder / m;
            r *= s; g *= s; b *= s;
        }
        return new Color32(B(r), B(g), B(b), 255);
    }

    static float ToSrgb(float v) => v <= 0f ? 0f : v < 0.0031308f ? v * 12.92f : 1.055f * Mathf.Pow(v, 1f / 2.4f) - 0.055f;

    static byte B(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

    // The satellite view with the walkable area marked: walkable kept bright, the rest dimmed, a light outline between.
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
                if (!map.IsWalkable(x, y)) { result[i] = Scale(p, 0.6f); continue; }
                bool edge = (x > 0 && !map.IsWalkable(x - 1, y)) || (x < w - 1 && !map.IsWalkable(x + 1, y)) ||
                            (y > 0 && !map.IsWalkable(x, y - 1)) || (y < h - 1 && !map.IsWalkable(x, y + 1));
                result[i] = edge ? Color32.Lerp(p, outline, 0.5f) : p;
            }
        return result;
    }

    static Color32 Scale(Color32 c, float f) =>
        new((byte)Mathf.Min(255, c.r * f), (byte)Mathf.Min(255, c.g * f), (byte)Mathf.Min(255, c.b * f), 255);
}
