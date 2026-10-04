using System;
using NivalisModKit;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisMinimap;

public enum Corner { BottomLeft, BottomRight, TopLeft, TopRight }

// Runs the HUD every frame.
internal class MinimapBehaviour : MonoBehaviour
{
    public MinimapBehaviour(IntPtr ptr) : base(ptr) { }

    public void Update()
    {
        try { Hud.Update(); }
        catch (Exception e) { Hud.Fail(e); }
    }
}

// The round minimap in a screen corner. The map image is built once per district (MapSource); each frame only moves
// and turns it: which part of the image shows (uvRect), its rotation, the arrow's rotation and the north marker.
// Visibility is the kit's Ui.GameHudAlpha: the game HUD's fade (dialogue, cutscenes), and hidden while a menu is open.
// Heading is the kit's Player.Heading (the game camera's; the body doesn't turn when the player looks around).
internal static class Hud
{
    // The map image is this much wider than the circle, so it still covers the circle when rotated.
    const float Overscan = 1.45f;
    const float Ring = 4f;

    static GameObject root;
    static CanvasGroup group;
    static RectTransform frame, mapRect, arrow, north;
    static RawImage mapImage;
    static MapImage map;
    static string sceneSeen;
    static float sceneCheckAt, buildAt = -1f;
    static bool forceRebuild, failed;
    static bool toggledOff;

    internal static void Fail(Exception e)
    {
        if (failed) return;   // once: a broken HUD must not spam the log every frame
        failed = true;
        Plugin.L.LogError($"Minimap: HUD stopped: {e}");
        if (root != null) root.SetActive(false);
    }

    // For tests (dev command "minimap state"): how visible the minimap is and whose map it holds.
    internal static object State() => new
    {
        alpha = group != null ? System.Math.Round(group.alpha, 2) : 0.0,
        mapScene = map?.Scene,
        scene = Scenes.Active,
        fullMapOpen = FullMap.IsOpen,
        hudAlpha = System.Math.Round(Ui.GameHudAlpha, 2),
    };

    // Redraw the current district's map (style changed, or the player asked).
    internal static void Rebuild()
    {
        forceRebuild = true;
        buildAt = Time.unscaledTime;
    }

    internal static void Update()
    {
        if (failed) return;
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && Plugin.ToggleKeyValue is { } key && kb[key].wasPressedThisFrame) toggledOff = !toggledOff;

        if (!GameEvents.IsInGame || !Plugin.Enabled.Value)
        {
            FullMap.Close();
            SetAlpha(0f);
            return;
        }

        // A new scene (district, interior, curfew): hide at once (the old map would be wrong here), and build its map
        // shortly after it settles. The minimap only shows again once that map is ready.
        if (Time.unscaledTime >= sceneCheckAt)
        {
            sceneCheckAt = Time.unscaledTime + 0.25f;
            string scene = Scenes.Active;
            if (scene != sceneSeen)
            {
                sceneSeen = scene;
                buildAt = Time.unscaledTime + 1.5f;
                if (map != null && map.Scene != scene) UnloadMap();
            }
        }
        if (buildAt >= 0f && Time.unscaledTime >= buildAt)
        {
            buildAt = -1f;
            LoadMap();
        }
        if (map == null) { FullMap.Close(); SetAlpha(0f); return; }
        Markers.ScanStep();

        var player = Player.Transform;
        float hudAlpha = Ui.GameHudAlpha;   // 0 over menus and the dev console

        // The full map: its key opens it during play (not over a menu) and closes it.
        if (kb != null && Plugin.FullMapKeyValue is { } fullKey && kb[fullKey].wasPressedThisFrame)
            FullMap.Toggle(map, hudAlpha > 0f && player != null);
        if (FullMap.IsOpen)
        {
            SetAlpha(0f);
            if (player != null) FullMap.Update(player.position, Player.Heading);
            return;
        }

        if (toggledOff) hudAlpha = 0f;
        SetAlpha(hudAlpha * Plugin.Opacity.Value);
        if (hudAlpha <= 0f || player == null) return;

        Layout();
        float heading = Player.Heading;
        float mapTurn = Plugin.RotateWithCamera.Value ? heading : 0f;

        // The part of the image around the player: Zoom metres across the circle (more across the overscanned image).
        var uv = map.ToUv(player.position);
        float metres = Plugin.Zoom.Value * Overscan;
        float uw = metres * map.PixelsPerMetre / map.Width, uh = metres * map.PixelsPerMetre / map.Height;
        mapImage.uvRect = new Rect(uv.x - uw / 2f, uv.y - uh / 2f, uw, uh);
        // Turning the image anticlockwise by the camera's heading puts what the camera faces at the top.
        mapRect.localEulerAngles = new Vector3(0f, 0f, mapTurn);
        float inner = Plugin.Size.Value - 2f * Ring;
        Markers.Update(player.position, mapTurn, inner / Plugin.Zoom.Value, inner / 2f, Plugin.Size.Value * 0.085f);
        arrow.localEulerAngles = new Vector3(0f, 0f, mapTurn - heading);

        // North marker on the rim (centred on the ring): map north is up, turned with the map.
        float radius = frame.rect.width / 2f - Ring / 2f;
        float a = mapTurn * Mathf.Deg2Rad;
        north.anchoredPosition = new Vector2(-Mathf.Sin(a), Mathf.Cos(a)) * radius;
        north.gameObject.SetActive(Plugin.RotateWithCamera.Value);
    }

    // The old scene's map, gone: nothing shows until the new one is loaded.
    static void UnloadMap()
    {
        FullMap.Close();
        if (map?.Texture != null) UnityEngine.Object.Destroy(map.Texture);
        map = null;
        Markers.Clear();
        SetAlpha(0f);
    }

    static void LoadMap()
    {
        FullMap.Close();
        var old = map;
        map = MapSource.Get(Plugin.Style.Value, Plugin.CutHeight.Value, forceRebuild);
        forceRebuild = false;
        if (old != null && old.Texture != null && (map == null || old.Texture != map.Texture)) UnityEngine.Object.Destroy(old.Texture);
        if (old == null || map == null || old.Scene != map.Scene) Markers.Clear();
        if (map == null) return;
        Build();
        mapImage.texture = map.Texture;
    }

    static void SetAlpha(float alpha)
    {
        if (group != null) group.alpha = alpha;
    }

    // Size and corner, from the settings (cheap enough to apply every frame, so changes show at once).
    static void Layout()
    {
        float size = Plugin.Size.Value;
        frame.sizeDelta = new Vector2(size, size);
        var c = Plugin.Corner.Value;
        float ax = c is Corner.BottomLeft or Corner.TopLeft ? 0f : 1f;
        float ay = c is Corner.BottomLeft or Corner.BottomRight ? 0f : 1f;
        frame.anchorMin = frame.anchorMax = frame.pivot = new Vector2(ax, ay);
        frame.anchoredPosition = new Vector2(ax == 0f ? Plugin.MarginX.Value : -Plugin.MarginX.Value,
                                             ay == 0f ? Plugin.MarginY.Value : -Plugin.MarginY.Value);
        float inner = size - 2f * Ring;
        mapRect.sizeDelta = new Vector2(inner * Overscan, inner * Overscan);
        arrow.sizeDelta = new Vector2(size * 0.077f, size * 0.077f);
        north.sizeDelta = new Vector2(size * 0.06f, size * 0.06f);
    }

    // ---------- building the UI (once) ----------

    static void Build()
    {
        if (root != null) return;
        root = new GameObject("NivalisMinimap_HUD");
        UnityEngine.Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        group = root.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;
        group.alpha = 0f;

        var circle = Sprites.Circle();
        frame = Rect("Frame", root.transform);
        var back = Rect("Back", frame);
        Stretch(back, 0f);
        var backImage = back.gameObject.AddComponent<Image>();
        backImage.sprite = circle;
        backImage.color = new Color(0.07f, 0.08f, 0.1f, 0.9f);

        var maskRect = Rect("Mask", frame);
        Stretch(maskRect, Ring);
        var maskImage = maskRect.gameObject.AddComponent<Image>();
        maskImage.sprite = circle;
        var mask = maskRect.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        mapRect = Rect("Map", maskRect);
        mapImage = mapRect.gameObject.AddComponent<RawImage>();
        Markers.Build(maskRect);

        var ring = Rect("Ring", frame);
        Stretch(ring, 0f);
        var ringImage = ring.gameObject.AddComponent<Image>();
        ringImage.sprite = Sprites.Ring();
        ringImage.color = new Color(0.85f, 0.88f, 0.92f, 0.95f);

        north = Rect("North", frame);
        var northImage = north.gameObject.AddComponent<Image>();
        northImage.sprite = circle;
        northImage.color = new Color(0.9f, 0.25f, 0.25f, 1f);

        arrow = Rect("Arrow", frame);
        var arrowImage = arrow.gameObject.AddComponent<Image>();
        arrowImage.sprite = Sprites.Arrow();
        arrowImage.color = new Color(1f, 0.85f, 0.3f, 1f);
        Layout();
    }

    static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        return rt;
    }

    static void Stretch(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }
}

// Shapes drawn at startup (no image files): a filled circle, a ring and an arrow, antialiased.
internal static class Sprites
{
    const int Size = 128;

    static Sprite circle;

    internal static Sprite Circle() => circle != null ? circle : circle = Make((x, y) => Coverage(Dist(x, y), 63.5f));

    internal static Sprite Ring() => Make((x, y) =>
    {
        float d = Dist(x, y);
        return Mathf.Min(Coverage(d, 63.5f), 1f - Coverage(d, 59.5f));
    });

    // Pointing up: a chevron with a notch at the back.
    internal static Sprite Arrow() => Make((x, y) =>
    {
        float px = (x + 0.5f) / Size - 0.5f, py = (y + 0.5f) / Size - 0.5f;   // -0.5..0.5, y up
        bool inside = py < 0.45f - Mathf.Abs(px) * 1.9f && py > -0.42f + Mathf.Abs(px) * 0.6f;
        return inside ? 1f : 0f;
    });

    static float Dist(int x, int y)
    {
        float dx = x + 0.5f - Size / 2f, dy = y + 0.5f - Size / 2f;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    static float Coverage(float d, float radius) => Mathf.Clamp01(radius - d + 0.5f);

    static Sprite Make(Func<int, int, float> alpha)
    {
        var pixels = new Color32[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                pixels[y * Size + x] = new Color32(255, 255, 255, (byte)(alpha(x, y) * 255f));
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f));
    }
}
