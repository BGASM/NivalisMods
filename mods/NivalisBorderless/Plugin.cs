using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace NivalisBorderless;

// Borderless windowed mode, which the game's settings don't offer: a normal window at the monitor's resolution with its
// title bar and border removed, covering the monitor. (Unity's own "fullscreen window" minimizes when the game loses
// focus in this build; a plain window doesn't.) The game applies its own screen setting when it starts, so this checks
// every 2 seconds and applies again if the window changed back. No kit needed.
[BepInPlugin("bgasm.nivalis.borderless", "Nivalis Borderless", "1.0.0")]
public class Plugin : BasePlugin
{
    internal static ConfigEntry<bool> Enabled;
    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        Enabled = Config.Bind("General", "Enabled", true,
            "Keep the game borderless windowed at your monitor's resolution. Off: the game's own screen setting applies (after a restart).");
        AddComponent<BorderlessBehaviour>();
    }
}

internal class BorderlessBehaviour : MonoBehaviour
{
    public BorderlessBehaviour(IntPtr ptr) : base(ptr) { }

    float checkAt = 3f;   // after the game has applied its own setting
    int applied;
    bool reportedError;
    IntPtr window;

    public void Update()
    {
        if (!Plugin.Enabled.Value || Time.unscaledTime < checkAt) return;
        checkAt = Time.unscaledTime + 2f;
        try
        {
            if (window == IntPtr.Zero) window = FindGameWindow();
            if (window == IntPtr.Zero) return;
            var m = MonitorRect(window);
            int w = m.Right - m.Left, h = m.Bottom - m.Top;

            // First a plain window at the monitor's size; the border comes off on the next check. Only the mode is
            // checked here: right after the border comes off Unity briefly reports the old framed size, and resizing
            // through Unity then would put the border back. The window's size is kept with Windows below.
            if (Screen.fullScreenMode != FullScreenMode.Windowed)
            {
                Log($"window {w}x{h} (was {Screen.fullScreenMode} {Screen.width}x{Screen.height})");
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
                return;
            }

            long style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
            GetWindowRect(window, out var r);
            bool bordered = (style & Frame) != 0;
            bool placed = r.Left == m.Left && r.Top == m.Top && r.Right == m.Right && r.Bottom == m.Bottom;
            if (!bordered && placed) return;

            Log($"removing the border at {m.Left},{m.Top} {w}x{h}");
            if (bordered) SetWindowLongPtr(window, GWL_STYLE, new IntPtr(style & ~Frame));
            SetWindowPos(window, IntPtr.Zero, m.Left, m.Top, w, h, SWP_FRAMECHANGED | SWP_NOZORDER | SWP_SHOWWINDOW);
        }
        catch (Exception e)
        {
            if (!reportedError) { reportedError = true; Plugin.L.LogWarning($"Borderless: {e.Message} (retrying every 2 s)"); }
        }
    }

    // Logged the first few times, then rarely: something applying its own mode again and again shows up here.
    void Log(string what)
    {
        applied++;
        if (applied <= 4 || applied % 30 == 0) Plugin.L.LogInfo($"Borderless: {what} (time {applied})");
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

    static RECT MonitorRect(IntPtr hwnd)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info);
        return info.rcMonitor;
    }

    // ---------- Win32 ----------
    const int GWL_STYLE = -16;
    const long Frame = 0x00C00000L /*WS_CAPTION*/ | 0x00040000L /*WS_THICKFRAME*/ | 0x00080000L /*WS_SYSMENU*/
                     | 0x00020000L /*WS_MINIMIZEBOX*/ | 0x00010000L /*WS_MAXIMIZEBOX*/;
    const uint SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
    const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
}
