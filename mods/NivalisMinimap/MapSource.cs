using System;
using System.IO;
using System.Text.Json;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NivalisMinimap;

public enum MapStyle { Satellite, Cut, Drawn }

// A district's minimap image and the numbers that place world positions on it.
internal sealed class MapImage
{
    public string Scene;
    public Texture2D Texture;
    public float MinX, MinZ, PixelsPerMetre;
    public int Width, Height;

    public Vector2 ToUv(Vector3 world) =>
        new((world.x - MinX) * PixelsPerMetre / Width, (world.z - MinZ) * PixelsPerMetre / Height);
}

// Builds each district's image once, on the player's machine, and keeps it in BepInEx\cache\NivalisMinimap\maps: the
// mod ships no pictures of the game. Rebuilt when the style, the game build or this code's render version changes,
// or on request (Refresh).
internal static class MapSource
{
    // Bump when the rendering changes, so cached maps are redrawn.
    const int RenderVersion = 1;

    static readonly Color32 Background = new(18, 20, 26, 255);

    sealed class CacheInfo
    {
        public int Version { get; set; }
        public string GameBuild { get; set; }
        public float MinX { get; set; }
        public float MinZ { get; set; }
        public float PixelsPerMetre { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    static string Dir => Path.Combine(Paths.CachePath, "NivalisMinimap", "maps");

    static string Key(string scene, MapStyle style, float cut) =>
        string.Join("_", scene.Split(Path.GetInvalidFileNameChars())) + "_" + style +
        (style == MapStyle.Cut ? $"_{cut:0.0}" : "");

    // The current district's map: from the cache, or built now (a fraction of a second, once). Null without a navmesh.
    internal static MapImage Get(MapStyle style, float cutHeight, bool rebuild)
    {
        string scene = SceneManager.GetActiveScene().name;
        string key = Key(scene, style, cutHeight);
        string png = Path.Combine(Dir, key + ".png"), json = Path.Combine(Dir, key + ".json");
        if (!rebuild && File.Exists(png) && File.Exists(json))
        {
            try
            {
                var info = JsonSerializer.Deserialize<CacheInfo>(File.ReadAllText(json));
                if (info != null && info.Version == RenderVersion && info.GameBuild == NivalisModKit.GameBuild.Fingerprint)
                {
                    var tex = NewTexture(info.Width, info.Height);
                    if (ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
                        return new MapImage
                        {
                            Scene = scene, Texture = tex, MinX = info.MinX, MinZ = info.MinZ,
                            PixelsPerMetre = info.PixelsPerMetre, Width = tex.width, Height = tex.height,
                        };
                    UnityEngine.Object.Destroy(tex);
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"Minimap: cached map {key} unreadable, redrawing: {e.Message}"); }
        }

        var started = DateTime.UtcNow;
        var map = NavMap.Build(4096, 10f);
        if (map == null) return null;
        Color32[] pixels = style == MapStyle.Drawn
            ? map.Pixels
            : FadeOutside(map, Shot.TakePhoto(map, new ShotOptions
            {
                PixelsPerMetre = map.PixelsPerMetre,
                CutAbove = style == MapStyle.Cut ? cutHeight : float.NaN,
            }).Pixels);
        Frame(pixels, map.Width, map.Height);

        var texture = NewTexture(map.Width, map.Height);
        texture.SetPixels32(pixels);
        texture.Apply();
        var image = new MapImage
        {
            Scene = scene, Texture = texture, MinX = map.Min.x, MinZ = map.Min.z,
            PixelsPerMetre = map.PixelsPerMetre, Width = map.Width, Height = map.Height,
        };
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(png, ImageConversion.EncodeToPNG(texture));
            File.WriteAllText(json, JsonSerializer.Serialize(new CacheInfo
            {
                Version = RenderVersion, GameBuild = NivalisModKit.GameBuild.Fingerprint, MinX = image.MinX,
                MinZ = image.MinZ, PixelsPerMetre = image.PixelsPerMetre, Width = image.Width, Height = image.Height,
            }));
        }
        catch (Exception e) { Plugin.L.LogWarning($"Minimap: couldn't cache {key}: {e.Message}"); }
        Plugin.L.LogInfo($"Minimap: drew {key} ({map.Width}x{map.Height}) in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
        return image;
    }

    static Texture2D NewTexture(int w, int h) =>
        new(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };

    // The low-detail scenery round the edges (built to be seen from the street) fades out beyond a few metres of
    // walkable ground: full photo within 3 m, background by 9 m.
    static Color32[] FadeOutside(NavMap map, Color32[] photo)
    {
        int w = map.Width, h = map.Height;
        var dist = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                dist[y * w + x] = map.IsWalkable(x, y) ? 0f : float.MaxValue;
        // Two-pass chamfer distance (pixels).
        const float d1 = 1f, d2 = 1.4142f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float v = dist[i];
                if (x > 0) v = Math.Min(v, dist[i - 1] + d1);
                if (y > 0)
                {
                    v = Math.Min(v, dist[i - w] + d1);
                    if (x > 0) v = Math.Min(v, dist[i - w - 1] + d2);
                    if (x < w - 1) v = Math.Min(v, dist[i - w + 1] + d2);
                }
                dist[i] = v;
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                float v = dist[i];
                if (x < w - 1) v = Math.Min(v, dist[i + 1] + d1);
                if (y < h - 1)
                {
                    v = Math.Min(v, dist[i + w] + d1);
                    if (x < w - 1) v = Math.Min(v, dist[i + w + 1] + d2);
                    if (x > 0) v = Math.Min(v, dist[i + w - 1] + d2);
                }
                dist[i] = v;
            }
        float start = 3f * map.PixelsPerMetre, end = 9f * map.PixelsPerMetre;
        var result = new Color32[photo.Length];
        for (int i = 0; i < photo.Length; i++)
        {
            float t = Mathf.Clamp01((dist[i] - start) / (end - start));
            result[i] = Color32.Lerp(photo[i], Background, t);
        }
        return result;
    }

    // A one-pixel background border: past the map's edge the texture repeats its edge pixels.
    static void Frame(Color32[] pixels, int w, int h)
    {
        for (int x = 0; x < w; x++) { pixels[x] = Background; pixels[(h - 1) * w + x] = Background; }
        for (int y = 0; y < h; y++) { pixels[y * w] = Background; pixels[y * w + w - 1] = Background; }
    }
}
