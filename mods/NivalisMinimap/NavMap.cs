using System;
using System.Linq;
using NivalisModKit;
using UnityEngine;

namespace NivalisMinimap;

// A district's navigation mesh (Navigation.Triangulate) drawn from above: the walkable ground, height shaded (higher
// is lighter, upper surfaces drawn over lower ones). Drawn onto a pixel grid given as an origin (world x, z of pixel
// 0,0) and pixels per metre, so it lines up with a Photo.TopDown of the same area: the drawn map style, and the
// walkable mask used to fade the photo's outskirts.
internal sealed class NavMap
{
    public Vector2 Origin;              // world x, z of pixel 0,0
    public float PixelsPerMetre;
    public int Width, Height;
    public Navigation.Mesh Mesh;
    public Color32[] Pixels;            // row 0 is the south edge (lowest z), as Texture2D stores it

    public static readonly Color32 Background = new(18, 20, 26, 255);
    // Area 0 is Unity's default "Walkable"; others are whatever the game defined.
    static readonly Color32[] AreaTint =
    {
        new(170, 185, 205, 255), new(120, 120, 120, 255), new(205, 170, 120, 255), new(140, 200, 150, 255),
        new(200, 140, 190, 255), new(130, 190, 210, 255), new(210, 210, 130, 255), new(190, 130, 130, 255),
    };

    public Vector2 ToPixel(Vector3 world) =>
        new((world.x - Origin.x) * PixelsPerMetre, (world.z - Origin.y) * PixelsPerMetre);

    // Drawn on the photo's grid.
    public static NavMap Draw(Navigation.Mesh mesh, PhotoResult grid) =>
        Draw(mesh, grid.Origin, grid.PixelsPerMetre, grid.Width, grid.Height);

    // Drawn over the mesh's own bounds, the longer side maxSize pixels (or at pixelsPerMetre, capped at maxSize).
    public static NavMap Draw(Navigation.Mesh mesh, int maxSize, float pixelsPerMetre = 0f)
    {
        var b = mesh.Bounds;
        float sizeX = Mathf.Max(b.size.x, 1f), sizeZ = Mathf.Max(b.size.z, 1f);
        float ppm = maxSize / Mathf.Max(sizeX, sizeZ);
        if (pixelsPerMetre > 0f) ppm = Mathf.Min(pixelsPerMetre, ppm);
        return Draw(mesh, new Vector2(b.min.x, b.min.z), ppm,
                    Mathf.Max(1, Mathf.CeilToInt(sizeX * ppm)), Mathf.Max(1, Mathf.CeilToInt(sizeZ * ppm)));
    }

    static NavMap Draw(Navigation.Mesh mesh, Vector2 origin, float ppm, int w, int h)
    {
        var map = new NavMap { Mesh = mesh, Origin = origin, PixelsPerMetre = ppm, Width = w, Height = h };
        map.Pixels = new Color32[w * h];
        Array.Fill(map.Pixels, Background);
        var v = mesh.Vertices;
        var idx = mesh.Indices;
        float minY = mesh.Bounds.min.y, yRange = Mathf.Max(mesh.Bounds.size.y, 0.01f);
        // Lowest first, so upper surfaces (bridges, upper floors) end up on top, as seen from above.
        var order = Enumerable.Range(0, mesh.Triangles)
            .OrderBy(t => v[idx[t * 3]].y + v[idx[t * 3 + 1]].y + v[idx[t * 3 + 2]].y).ToArray();
        foreach (int t in order)
        {
            var a = v[idx[t * 3]]; var b = v[idx[t * 3 + 1]]; var c = v[idx[t * 3 + 2]];
            float height = ((a.y + b.y + c.y) / 3f - minY) / yRange;
            var tint = AreaTint[Math.Abs(mesh.Areas[t]) % AreaTint.Length];
            float shade = Mathf.Lerp(0.45f, 1f, height);
            var colour = new Color32((byte)(tint.r * shade), (byte)(tint.g * shade), (byte)(tint.b * shade), 255);
            map.Fill(map.ToPixel(a), map.ToPixel(b), map.ToPixel(c), colour);
        }
        return map;
    }

    // Fills every pixel whose centre is inside the triangle (either winding).
    void Fill(Vector2 a, Vector2 b, Vector2 c, Color32 colour)
    {
        int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
        int x1 = Mathf.Min(Width - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
        int y1 = Mathf.Min(Height - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
        float area = Edge(a, b, c);
        if (Mathf.Abs(area) < 1e-6f)
        {
            // Thinner than a pixel: still mark it, so narrow walkways don't vanish at low resolution.
            Plot(Mathf.RoundToInt(a.x), Mathf.RoundToInt(a.y), colour);
            return;
        }
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float w0 = Edge(b, c, p), w1 = Edge(c, a, p), w2 = Edge(a, b, p);
                if (area > 0 ? (w0 >= 0 && w1 >= 0 && w2 >= 0) : (w0 <= 0 && w1 <= 0 && w2 <= 0))
                    Pixels[y * Width + x] = colour;
            }
    }

    static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

    void Plot(int x, int y, Color32 colour)
    {
        if (x >= 0 && y >= 0 && x < Width && y < Height) Pixels[y * Width + x] = colour;
    }

    public bool IsWalkable(int x, int y)
    {
        var c = Pixels[y * Width + x];
        return c.r != Background.r || c.g != Background.g || c.b != Background.b;
    }

    // A filled dot with a short line showing which way it faces (a marker for testing the mapping).
    public void Marker(Vector3 world, float heading, Color32 colour, int radius = 5)
    {
        var p = ToPixel(world);
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                if (dx * dx + dy * dy <= radius * radius) Plot(Mathf.RoundToInt(p.x) + dx, Mathf.RoundToInt(p.y) + dy, colour);
        float a = heading * Mathf.Deg2Rad;
        var dir = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
        for (int s = radius; s <= radius * 3; s++)
            Plot(Mathf.RoundToInt(p.x + dir.x * s), Mathf.RoundToInt(p.y + dir.y * s), colour);
    }

    public byte[] EncodePng() => EncodePng(Pixels, Width, Height);

    public static byte[] EncodePng(Color32[] pixels, int width, int height)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        try
        {
            tex.SetPixels32(pixels);
            tex.Apply();
            return ImageConversion.EncodeToPNG(tex);
        }
        finally { UnityEngine.Object.Destroy(tex); }
    }
}
