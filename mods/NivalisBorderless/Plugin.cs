using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace NivalisBorderless;

// Borderless windowed mode, which the game's settings don't offer: Unity's "fullscreen window" at the monitor's native
// resolution. The game applies its own screen setting when it starts, so this applies borderless a few seconds in and
// again whenever the mode changes back. No kit needed.
[BepInPlugin("bgasm.nivalis.borderless", "Nivalis Borderless", "1.0.0")]
public class Plugin : BasePlugin
{
    internal static ConfigEntry<bool> Enabled;

    public override void Load()
    {
        Enabled = Config.Bind("General", "Enabled", true,
            "Keep the game borderless windowed at your monitor's resolution. Off: the game's own screen setting applies (after a restart).");
        AddComponent<BorderlessBehaviour>();
    }
}

internal class BorderlessBehaviour : MonoBehaviour
{
    public BorderlessBehaviour(IntPtr ptr) : base(ptr) { }

    float checkAt = 3f;   // after the game has applied its own setting

    public void Update()
    {
        if (!Plugin.Enabled.Value || Time.unscaledTime < checkAt) return;
        checkAt = Time.unscaledTime + 2f;
        try
        {
            var display = Display.main;
            int w = display.systemWidth, h = display.systemHeight;
            if (Screen.fullScreenMode == FullScreenMode.FullScreenWindow && Screen.width == w && Screen.height == h) return;
            Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
        }
        catch { /* try again in 2 s */ }
    }
}
