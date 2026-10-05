using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using NivalisModKit;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisCursor;

// A free mouse cursor on demand (middle mouse button by default): the kit's menu mode, as when a game screen is open,
// so the cursor shows and movement and mouse-look pause. Press again to go back. Handy with the game borderless next to
// a browser or another window.
[BepInPlugin(Guid, "Nivalis Cursor", "1.0.0")]
[BepInDependency(ModKit.Guid, ">=0.2.0")]   // Ui.RequestMenuMode
public class Plugin : BasePlugin
{
    internal const string Guid = "bgasm.nivalis.cursor";
    internal static ConfigEntry<string> Button;

    public override void Load()
    {
        Button = Config.Bind("General", "Button", "Middle",
            "What toggles the cursor: Middle, Back or Forward (mouse buttons), or a keyboard key name (e.g. F8, LeftAlt).");
        ModMenu.ListSettings(Guid);
        GameEvents.GameEnded += CursorToggle.Release;
        AddComponent<CursorToggle>();
    }
}

internal class CursorToggle : MonoBehaviour
{
    public CursorToggle(IntPtr ptr) : base(ptr) { }

    static IDisposable menuMode;

    internal static void Release()
    {
        menuMode?.Dispose();
        menuMode = null;
    }

    public void Update()
    {
        if (!Pressed()) return;
        if (menuMode != null) { Release(); return; }
        // Only during play, not over the game's own screens (they already give a cursor).
        if (!GameEvents.IsInGame || Ui.IsMenuModeRequested) return;
        menuMode = Ui.RequestMenuMode(Plugin.Guid);
    }

    static bool Pressed()
    {
        string b = Plugin.Button.Value?.Trim() ?? "";
        var mouse = Mouse.current;
        switch (b.ToLowerInvariant())
        {
            case "middle": return mouse != null && mouse.middleButton.wasPressedThisFrame;
            case "back": return mouse != null && mouse.backButton.wasPressedThisFrame;
            case "forward": return mouse != null && mouse.forwardButton.wasPressedThisFrame;
        }
        var kb = Keyboard.current;
        return kb != null && Enum.TryParse(b, true, out Key key) && key != Key.None && kb[key].wasPressedThisFrame;
    }
}
