using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using NivalisModKit;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisMinimap;

// The whole district on one screen (F7 by default): north up, the player's arrow, every vendor stall, a name on hover.
// Mouse wheel zooms towards the cursor, dragging pans. Holds the kit's menu mode while open (cursor on, movement off;
// Escape closes this instead of opening the pause menu). Shows the same cached image as the minimap.
internal static class FullMap
{
    const string Owner = "bgasm.nivalis.minimap";
    const float MaxPixelsPerMetreZoom = 0.04f;   // most zoomed in: 4 cm per screen unit (the image is 10 cm per pixel)

    static GameObject root;
    static Canvas canvas;
    static RectTransform view, arrow, badgeLayer, tip;
    static RawImage image;
    static TextMeshProUGUI title, tipText, hint;
    static IDisposable menu;
    static MapImage map;
    static readonly List<(RectTransform badge, Markers.Marker vendor)> badges = new();

    // The view: centre in world metres (x, z) and metres per canvas unit.
    static float centreX, centreZ, metresPerUnit, fitMetresPerUnit;

    internal static bool IsOpen => menu != null;

    internal static void Toggle(MapImage current, bool canOpen)
    {
        if (IsOpen) Close();
        else if (canOpen && current != null) Open(current);
    }

    internal static void Close()
    {
        menu?.Dispose();
        menu = null;
        if (root != null) root.SetActive(false);
    }

    static void Open(MapImage current)
    {
        map = current;
        Build();
        root.SetActive(true);
        image.texture = map.Texture;
        title.text = DistrictName();
        foreach (var (b, _) in badges) if (b != null) UnityEngine.Object.Destroy(b.gameObject);
        badges.Clear();
        foreach (var v in Markers.All)
            if (v.Badge != null || v.Stall != null) badges.Add((Markers.MakeBadge(v, badgeLayer), v));

        // Start fitted to the whole district.
        var size = ViewSize();
        float widthM = map.Width / map.PixelsPerMetre, heightM = map.Height / map.PixelsPerMetre;
        fitMetresPerUnit = Mathf.Max(widthM / size.x, heightM / size.y);
        metresPerUnit = fitMetresPerUnit;
        centreX = map.MinX + widthM / 2f;
        centreZ = map.MinZ + heightM / 2f;
        menu = Ui.RequestMenuMode(Owner);
    }

    // Each frame while open: input, then everything placed from the view.
    internal static void Update(Vector3 player, float heading)
    {
        if (!IsOpen) return;
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { Close(); return; }
        var mouse = UnityEngine.InputSystem.Mouse.current;
        var size = ViewSize();
        Vector2 cursor = Vector2.zero;
        if (mouse != null)
        {
            // Relative to the view's centre (the frame sits a little below the screen's centre).
            cursor = (mouse.position.ReadValue() - new Vector2(Screen.width, Screen.height) / 2f) / canvas.scaleFactor - ViewOffset();
            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0f && Mathf.Abs(cursor.x) <= size.x / 2f && Mathf.Abs(cursor.y) <= size.y / 2f)
            {
                // Zoom about the cursor: the point under it stays put.
                float wx = centreX + cursor.x * metresPerUnit, wz = centreZ + cursor.y * metresPerUnit;
                metresPerUnit = Mathf.Clamp(metresPerUnit * Mathf.Pow(0.85f, Mathf.Sign(scroll)),
                                            MaxPixelsPerMetreZoom, fitMetresPerUnit * 1.25f);
                centreX = wx - cursor.x * metresPerUnit;
                centreZ = wz - cursor.y * metresPerUnit;
            }
            if (mouse.leftButton.isPressed)
            {
                var delta = mouse.delta.ReadValue() / canvas.scaleFactor;
                centreX -= delta.x * metresPerUnit;
                centreZ -= delta.y * metresPerUnit;
            }
        }

        // The image window that shows exactly the view.
        float uw = size.x * metresPerUnit * map.PixelsPerMetre / map.Width;
        float uh = size.y * metresPerUnit * map.PixelsPerMetre / map.Height;
        var c = map.ToUv(new Vector3(centreX, 0f, centreZ));
        image.uvRect = new Rect(c.x - uw / 2f, c.y - uh / 2f, uw, uh);

        arrow.anchoredPosition = ToView(player);
        arrow.localEulerAngles = new Vector3(0f, 0f, -heading);
        arrow.gameObject.SetActive(Inside(arrow.anchoredPosition, size, 0f));

        // Vendors, and the one under the cursor named.
        Markers.Marker hovered = null;
        Vector2 hoveredAt = default;
        float best = 18f;
        foreach (var (badge, v) in badges)
        {
            if (badge == null) continue;
            var p = ToView(v.Position);
            bool inside = Inside(p, size, 0f);
            badge.gameObject.SetActive(inside);
            if (!inside) continue;
            badge.anchoredPosition = p;
            badge.sizeDelta = new Vector2(30f, 30f);
            float d = (p - cursor).magnitude;
            if (d < best) { best = d; hovered = v; hoveredAt = p; }
        }
        tip.gameObject.SetActive(hovered != null);
        if (hovered != null)
        {
            string tipName = WithoutDistrict(hovered.Name);
            tipText.text = string.IsNullOrEmpty(hovered.Detail) ? tipName : $"{tipName}\n<size=75%><alpha=#AA>{hovered.Detail}";
            tip.anchoredPosition = hoveredAt + new Vector2(20f, 20f);
        }
    }

    static Vector2 ToView(Vector3 world) =>
        new((world.x - centreX) / metresPerUnit, (world.z - centreZ) / metresPerUnit);

    static bool Inside(Vector2 p, Vector2 size, float margin) =>
        Mathf.Abs(p.x) <= size.x / 2f + margin && Mathf.Abs(p.y) <= size.y / 2f + margin;

    static Vector2 ViewSize() => view.rect.size;

    // Where the view's centre is, from the canvas's centre, in canvas units.
    static Vector2 ViewOffset()
    {
        var canvasSize = root.GetComponent<RectTransform>().rect.size;
        var frame = view.parent.GetComponent<RectTransform>();
        var mid = (frame.anchorMin + frame.anchorMax) / 2f - new Vector2(0.5f, 0.5f);
        return Vector2.Scale(mid, canvasSize);
    }

    // "Meridian Market Greengrocer" -> "Greengrocer" while in Meridian Market: the district is the map's title.
    static string WithoutDistrict(string name)
    {
        string district = DistrictName();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(district)) return name;
        string trimmed = Regex.Replace(name, Regex.Escape(district), "", RegexOptions.IgnoreCase);
        trimmed = Regex.Replace(trimmed, @"\s{2,}", " ").Trim(' ', '-', ',', ':', '(', ')');
        return string.IsNullOrEmpty(trimmed) ? name : trimmed;
    }

    // "1_Lowtown" -> "Lowtown", "2_Meridian_Market" -> "Meridian Market".
    static string DistrictName() => Regex.Replace(map.Scene ?? "", @"^\d+_", "").Replace('_', ' ');

    // ---------- UI (built once) ----------

    static void Build()
    {
        if (root != null) return;
        root = new GameObject("NivalisMinimap_FullMap");
        UnityEngine.Object.DontDestroyOnLoad(root);
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;   // over the game's HUD; the minimap hides while this is open
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var backdrop = Rect("Backdrop", root.transform);
        Stretch(backdrop);
        backdrop.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.78f);

        var frame = Rect("Frame", root.transform);
        frame.anchorMin = new Vector2(0.08f, 0.09f);
        frame.anchorMax = new Vector2(0.92f, 0.87f);
        frame.offsetMin = frame.offsetMax = Vector2.zero;
        frame.gameObject.AddComponent<Image>().color = new Color(0.85f, 0.88f, 0.92f, 0.9f);

        view = Rect("View", frame);
        Stretch(view);
        view.offsetMin = new Vector2(3f, 3f);
        view.offsetMax = new Vector2(-3f, -3f);
        view.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.08f, 0.1f, 1f);
        view.gameObject.AddComponent<RectMask2D>();

        var imageRect = Rect("Map", view);
        Stretch(imageRect);
        image = imageRect.gameObject.AddComponent<RawImage>();

        badgeLayer = Rect("Vendors", view);
        badgeLayer.sizeDelta = Vector2.zero;

        arrow = Rect("Arrow", view);
        arrow.sizeDelta = new Vector2(26f, 26f);
        var arrowImage = arrow.gameObject.AddComponent<Image>();
        arrowImage.sprite = Sprites.Arrow();
        arrowImage.color = new Color(1f, 0.85f, 0.3f, 1f);

        var font = GameFont();
        title = Text("Title", root.transform, font, 34f, TextAlignmentOptions.Center);
        var titleRect = title.rectTransform;
        titleRect.anchorMin = new Vector2(0.08f, 0.87f);
        titleRect.anchorMax = new Vector2(0.92f, 0.95f);
        titleRect.offsetMin = titleRect.offsetMax = Vector2.zero;

        hint = Text("Hint", root.transform, font, 20f, TextAlignmentOptions.Center);
        hint.text = "Scroll to zoom  ·  drag to move  ·  F7 or Esc to close";
        hint.color = new Color(1f, 1f, 1f, 0.7f);
        var hintRect = hint.rectTransform;
        hintRect.anchorMin = new Vector2(0.08f, 0.03f);
        hintRect.anchorMax = new Vector2(0.92f, 0.08f);
        hintRect.offsetMin = hintRect.offsetMax = Vector2.zero;

        tip = Rect("Tip", view);
        tip.pivot = new Vector2(0f, 0f);
        tip.sizeDelta = new Vector2(320f, 64f);
        var tipBack = tip.gameObject.AddComponent<Image>();
        tipBack.color = new Color(0.05f, 0.06f, 0.08f, 0.92f);
        var fitter = tip.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var tipLayout = tip.gameObject.AddComponent<HorizontalLayoutGroup>();
        tipLayout.padding = new RectOffset(10, 10, 6, 6);
        tipText = Text("Text", tip, font, 22f, TextAlignmentOptions.Left);
        tip.gameObject.SetActive(false);
        root.SetActive(false);
    }

    // A font the game already uses (TextMeshPro needs one to draw text).
    static TMP_FontAsset GameFont()
    {
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<TextMeshProUGUI>()))
        {
            var t = o.TryCast<TextMeshProUGUI>();
            if (t != null && t.font != null) return t.font;
        }
        return null;
    }

    static TextMeshProUGUI Text(string name, Transform parent, TMP_FontAsset font, float size, TextAlignmentOptions align)
    {
        var rt = Rect(name, parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.fontSize = size;
        t.alignment = align;
        t.color = Color.white;
        t.enableWordWrapping = false;
        t.raycastTarget = false;
        return t;
    }

    static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        return rt;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
