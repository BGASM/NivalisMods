using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace NivalisBorderless;

// Keeps the game on screen when you click another window. Since patch 4 the game's own Display Mode has
// "Fullscreen Window": a borderless window at the monitor's resolution, but Unity minimizes it as soon as the game
// loses focus. 1.0 made its own borderless window instead, which fights the game's new setting (the window kept
// changing back, and froze the game for some players). This version leaves the game's mode alone: in Fullscreen
// Window it stops Unity from hearing that the game lost focus, so it never minimizes. Windowed and Exclusive
// Fullscreen are left as the game makes them. No kit needed.
[BepInPlugin("bgasm.nivalis.borderless", "Nivalis Borderless", "1.0.1")]
public class Plugin : BasePlugin
{
    internal static ConfigEntry<bool> Enabled;
    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        Enabled = Config.Bind("General", "Enabled", true,
            "Keep the game on screen when you click another window. Set the game's Display Mode to Fullscreen Window.");
        AddComponent<BorderlessBehaviour>();
    }
}

internal class BorderlessBehaviour : MonoBehaviour
{
    public BorderlessBehaviour(IntPtr ptr) : base(ptr) { }

    // The game window's message handler is replaced once; the delegate must stay alive while it's installed.
    static WndProc hook;
    static IntPtr original, window;
    static bool active;            // Fullscreen Window and enabled: focus loss is hidden from Unity
    static bool reportedError;
    float checkAt = 2f;
    FullScreenMode lastMode = (FullScreenMode)(-1);

    public void Update()
    {
        if (Time.unscaledTime < checkAt) return;
        checkAt = Time.unscaledTime + 1f;
        try
        {
            if (window == IntPtr.Zero)
            {
                window = FindGameWindow();
                if (window == IntPtr.Zero) return;
                hook = Hook;
                original = SetWindowLongPtr(window, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(hook));
                if (original == IntPtr.Zero) throw new Exception($"could not hook the game window (error {Marshal.GetLastWin32Error()})");
            }

            var mode = Screen.fullScreenMode;
            active = Plugin.Enabled.Value && mode == FullScreenMode.FullScreenWindow;
            if (mode != lastMode)
            {
                lastMode = mode;
                Plugin.L.LogInfo(active
                    ? "Borderless: Fullscreen Window: the game stays on screen when you click another window"
                    : $"Borderless: Display Mode is {mode}: left as the game makes it" +
                      (Plugin.Enabled.Value ? " (choose Fullscreen Window in the game's settings)" : " (mod disabled)"));
            }
        }
        catch (Exception e)
        {
            if (!reportedError) { reportedError = true; Plugin.L.LogWarning($"Borderless: {e.Message}"); }
        }
    }

    // Unity minimizes a Fullscreen Window when it's told the app lost focus. While active, those messages stop here;
    // everything else, and gaining focus, goes to Unity as normal.
    static IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (active)
        {
            if (msg == WM_ACTIVATEAPP && wParam == IntPtr.Zero) return IntPtr.Zero;
            if (msg == WM_ACTIVATE && ((long)wParam & 0xFFFF) == WA_INACTIVE) return IntPtr.Zero;
        }
        return CallWindowProc(original, hwnd, msg, wParam, lParam);
    }

    static IntPtr FindGameWindow()
    {
        var main = Process.GetCurrentProcess().MainWindowHandle;
        if (main != IntPtr.Zero) return main;
        IntPtr found = IntPtr.Zero;
        uint pid = (uint)Process.GetCurrentProcess().Id;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    // ---------- Win32 ----------
    const int GWLP_WNDPROC = -4;
    const uint WM_ACTIVATE = 0x0006, WM_ACTIVATEAPP = 0x001C;
    const long WA_INACTIVE = 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
}
