using System.Runtime.InteropServices;

namespace Bujo.Lock;

/// <summary>
/// Le strict minimum de Win32. Aucun hook clavier bas niveau, aucun hook global :
/// c'est précisément la frontière entre "fenêtre pénible" et "logiciel malveillant".
/// </summary>
internal static partial class Native
{
    public const int WM_SYSCOMMAND = 0x0112;
    public const int WM_CLOSE      = 0x0010;
    public const int SC_MINIMIZE   = 0xF020;
    public const int SC_CLOSE      = 0xF060;
    public const int SC_MAXIMIZE   = 0xF030;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int SW_SHOW    = 5;
    public const int SW_RESTORE = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    /// <summary>
    /// Windows refuse SetForegroundWindow à un processus qui n'a pas le focus.
    /// Le contournement documenté : s'attacher temporairement à la file d'entrée
    /// du thread au premier plan, ce qui rend l'appel légitime aux yeux du système.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hwnd) return;

        var targetThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var currentThread = GetCurrentThreadId();
        var attached = targetThread != 0 && targetThread != currentThread
                       && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, targetThread, false);
        }
    }

    /// <summary>Recolle la fenêtre en topmost sans lui voler le focus (appel à haute fréquence).</summary>
    public static void PinTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
}
