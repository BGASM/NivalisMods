using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace NivalisMinimap;

// A district's map, drawn from its navigation mesh: the walkable surfaces NPCs path over, seen from above.
//
// The same numbers that place triangles in the image place anything else: a world position's x and z, minus the mesh's
// lower corner, times pixels per metre, is its pixel. Height (y) is dropped, except to shade higher ground lighter and
// to draw higher surfaces over lower ones.
internal sealed class NavMap
{
    public Vector3 Min, Max;            // mesh bounds, world metres
    public float PixelsPerMetre;
    public int Width, Height;
    public int Vertices, Triangles;
    public Dictionary<int, int> TrianglesByArea = new();
    public Color32[] Pixels;            // row 0 is the south edge (lowest z), as Texture2D stores it

    static readonly Color32 Background = new(18, 20, 26, 255);
    // Area 0 is Unity's default "Walkable"; others are whatever the game defined (roads, grass...).
    static readonly Color32[] AreaTint =
    {
        new(170, 185, 205, 255), new(120, 120, 120, 255), new(205, 170, 120, 255), new(140, 200, 150, 255),
        new(200, 140, 190, 255), new(130, 190, 210, 255), new(210, 210, 130, 255), new(190, 130, 130, 255),
    };

    public Vector2 ToPixel(Vector3 world) =>
        new((world.x - Min.x) * PixelsPerMetre, (world.z - Min.z) * PixelsPerMetre);

    // Everything loaded right now (Unity merges every loaded navmesh), fitted so the longer side is maxSize pixels,
    // or at pixelsPerMetre if given (capped at maxSize).
    public static NavMap Build(int maxSize, float pixelsPerMetre = 0f)
    {
        var (srcV, srcI, srcA) = Triangulate();
        if (srcV == null || srcI == null || srcV.Length == 0 || srcI.Length < 3) return null;

        // Copy out of the game's arrays once; reading them element by element across the interop boundary is slow.
        var v = new Vector3[srcV.Length];
        for (int i = 0; i < v.Length; i++) v[i] = srcV[i];
        var idx = new int[srcI.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = srcI[i];
        int triCount = idx.Length / 3;
        var areas = new int[triCount];
        if (srcA != null)
            for (int i = 0; i < triCount && i < srcA.Length; i++) areas[i] = srcA[i];

        var map = new NavMap { Vertices = v.Length, Triangles = triCount };
        map.Min = map.Max = v[0];
        foreach (var p in v) { map.Min = Vector3.Min(map.Min, p); map.Max = Vector3.Max(map.Max, p); }
        foreach (var a in areas) map.TrianglesByArea[a] = map.TrianglesByArea.TryGetValue(a, out var n) ? n + 1 : 1;

        float sizeX = Mathf.Max(map.Max.x - map.Min.x, 1f), sizeZ = Mathf.Max(map.Max.z - map.Min.z, 1f);
        map.PixelsPerMetre = maxSize / Mathf.Max(sizeX, sizeZ);
        if (pixelsPerMetre > 0f) map.PixelsPerMetre = Mathf.Min(pixelsPerMetre, map.PixelsPerMetre);
        map.Width = Mathf.Max(1, Mathf.CeilToInt(sizeX * map.PixelsPerMetre));
        map.Height = Mathf.Max(1, Mathf.CeilToInt(sizeZ * map.PixelsPerMetre));
        map.Pixels = new Color32[map.Width * map.Height];
        Array.Fill(map.Pixels, Background);

        // Lowest first, so upper surfaces (bridges, upper floors) end up on top, as seen from above.
        float yRange = Mathf.Max(map.Max.y - map.Min.y, 0.01f);
        var order = Enumerable.Range(0, triCount)
            .OrderBy(t => v[idx[t * 3]].y + v[idx[t * 3 + 1]].y + v[idx[t * 3 + 2]].y).ToArray();
        foreach (int t in order)
        {
            var a = v[idx[t * 3]]; var b = v[idx[t * 3 + 1]]; var c = v[idx[t * 3 + 2]];
            float height = ((a.y + b.y + c.y) / 3f - map.Min.y) / yRange;
            var tint = AreaTint[Math.Abs(areas[t]) % AreaTint.Length];
            float shade = Mathf.Lerp(0.45f, 1f, height);
            var colour = new Color32((byte)(tint.r * shade), (byte)(tint.g * shade), (byte)(tint.b * shade), 255);
            map.Fill(map.ToPixel(a), map.ToPixel(b), map.ToPixel(c), colour);
        }
        return map;
    }

    // NavMesh.CalculateTriangulation was stripped from the game's build (it never calls it), and its result type with it,
    // but the engine still registers the native function. It fills a NavMeshTriangulation: three managed arrays,
    // vertices (Vector3[]), indices (int[], three per triangle) and areas (int[], one per triangle), in that order.
    [StructLayout(LayoutKind.Sequential)]
    struct RawTriangulation { public IntPtr Vertices, Indices, Areas; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void TriangulateFn(out RawTriangulation result);

    static TriangulateFn triangulate;

    static (Il2CppStructArray<Vector3>, Il2CppStructArray<int>, Il2CppStructArray<int>) Triangulate()
    {
        triangulate ??= IL2CPP.ResolveICall<TriangulateFn>("UnityEngine.AI.NavMesh::CalculateTriangulation_Injected");
        triangulate(out var raw);
        return (raw.Vertices == IntPtr.Zero ? null : new Il2CppStructArray<Vector3>(raw.Vertices),
                raw.Indices == IntPtr.Zero ? null : new Il2CppStructArray<int>(raw.Indices),
                raw.Areas == IntPtr.Zero ? null : new Il2CppStructArray<int>(raw.Areas));
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

    // A filled dot with a short line showing which way it faces (a marker for testing the mapping).
    public void Marker(Vector3 world, Vector3 forward, Color32 colour, int radius = 5)
    {
        var p = ToPixel(world);
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                if (dx * dx + dy * dy <= radius * radius) Plot(Mathf.RoundToInt(p.x) + dx, Mathf.RoundToInt(p.y) + dy, colour);
        var dir = new Vector2(forward.x, forward.z);
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();
        for (int s = radius; s <= radius * 3; s++)
            Plot(Mathf.RoundToInt(p.x + dir.x * s), Mathf.RoundToInt(p.y + dir.y * s), colour);
    }

    public bool IsWalkable(int x, int y)
    {
        var c = Pixels[y * Width + x];
        return c.r != Background.r || c.g != Background.g || c.b != Background.b;
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
