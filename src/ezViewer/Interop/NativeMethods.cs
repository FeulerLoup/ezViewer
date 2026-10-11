using System;
using System.Runtime.InteropServices;

namespace EzViewer.Interop;

internal static class NativeMethods
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int StrCmpLogicalW(string psz1, string psz2);

    [DllImport("kernel32.dll")]
    public static extern uint GetACP();

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>临时关闭窗口的最大化/还原动画。</summary>
    public static void SetTransitionsEnabled(IntPtr hwnd, bool enabled)
    {
        try
        {
            int disabled = enabled ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
        }
        catch
        {
        }
    }

    public static WINDOWPLACEMENT GetPlacement(IntPtr hwnd)
    {
        var p = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref p);
        return p;
    }

    public static void RestorePlacement(IntPtr hwnd, WINDOWPLACEMENT placement)
    {
        placement.length = Marshal.SizeOf<WINDOWPLACEMENT>();
        placement.flags = 0;
        if (placement.showCmd == SW_SHOWMINIMIZED) placement.showCmd = SW_SHOWNORMAL;
        SetWindowPlacement(hwnd, ref placement);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    public static RECT GetWindowBounds(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return r;
    }

    /// <summary>直接设置窗口矩形（物理像素）。贴靠在屏幕半边的窗口也能按原样恢复。</summary>
    public static void SetWindowBounds(IntPtr hwnd, RECT r) =>
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);

#if DEBUG
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    public static bool DebugIsZoomed(IntPtr hwnd) => IsZoomed(hwnd);

    public static string DebugWindowRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return $"({r.Left},{r.Top},{r.Right},{r.Bottom})";
    }
#endif

    private static bool TryGetMonitor(IntPtr hwnd, out MONITORINFO info)
    {
        info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info);
    }

    /// <summary>把窗口铺满它所在的显示器（物理像素）。</summary>
    public static void CoverMonitor(IntPtr hwnd)
    {
        if (!TryGetMonitor(hwnd, out var info)) return;
        var r = info.rcMonitor;
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 让最大化的窗口一步还原成铺满显示器的普通窗口，不经过原来的还原尺寸。
    /// WINDOWPLACEMENT 用的是工作区坐标：屏幕坐标减去任务栏在该显示器上占的偏移。
    /// </summary>
    public static void UnmaximizeToMonitor(IntPtr hwnd, WINDOWPLACEMENT placement)
    {
        if (!TryGetMonitor(hwnd, out var info)) return;
        int dx = info.rcWork.Left - info.rcMonitor.Left;
        int dy = info.rcWork.Top - info.rcMonitor.Top;
        var m = info.rcMonitor;
        placement.length = Marshal.SizeOf<WINDOWPLACEMENT>();
        placement.flags = 0;
        placement.showCmd = SW_SHOWNORMAL;
        placement.rcNormalPosition = new RECT { Left = m.Left - dx, Top = m.Top - dy, Right = m.Right - dx, Bottom = m.Bottom - dy };
        SetWindowPlacement(hwnd, ref placement);
    }

    public static void TryEnableDarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
        }
        catch
        {
        }
    }
}
