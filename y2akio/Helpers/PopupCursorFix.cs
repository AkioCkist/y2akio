using System;
using System.Runtime.InteropServices;
using System.Text;

namespace y2akio.Helpers;

/// <summary>
/// Works around a Windows App SDK glitch: windowed popups (ComboBox drop-downs, flyouts, menus) live in
/// their own HWND of class <c>Microsoft.UI.Content.PopupWindowSiteBridge</c>, registered with NO class
/// cursor. When such a popup opens under a stationary mouse, Windows has no cursor to apply and keeps
/// showing the busy (IDC_WAIT) cursor until the mouse moves. This watches for those windows being
/// created in our process and gives their class the normal arrow cursor (XAML still overrides it per
/// element, e.g. the I-beam over text).
/// </summary>
public static class PopupCursorFix
{
    private const string PopupWindowClass = "Microsoft.UI.Content.PopupWindowSiteBridge";

    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int GCLP_HCURSOR = -12;
    private const int IDC_ARROW = 32512;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    // Kept in a static field so the GC never collects the delegate while native code still calls it.
    private static readonly WinEventProc Callback = OnWinEvent;
    private static IntPtr _hook;
    private static IntPtr _arrow;

    /// <summary>Installs the hook. Call once from the UI thread (callbacks arrive via its message loop).</summary>
    public static void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _arrow = LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW);
        _hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, Callback,
            (uint)Environment.ProcessId, 0, WINEVENT_OUTOFCONTEXT);
    }

    public static void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != 0) return;

        var name = new StringBuilder(64);
        if (GetClassName(hwnd, name, name.Capacity) == 0 || name.ToString() != PopupWindowClass) return;

        // Class-wide: the first popup fixes every later one, but stay cheap and idempotent regardless.
        if (GetClassLongPtr(hwnd, GCLP_HCURSOR) == IntPtr.Zero)
            SetClassLongPtr(hwnd, GCLP_HCURSOR, _arrow);

        // The popup may already be under the mouse: refresh the cursor right away instead of on next move.
        if (evt == EVENT_OBJECT_SHOW && GetCursorPos(out var pt) && WindowFromPoint(pt) == hwnd)
            SetCursor(_arrow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);
}
