using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MinterWallet;

// A notification of the system: on Windows from the wallet's own tray icon (Shell_NotifyIcon with NIF_INFO, which
// Windows 10 and 11 show as a toast), on Linux through notify-send (libnotify). Avalonia's tray icon belongs to its
// hidden top-level "AvaloniaMessageWindow …" under a small id (1 for the first icon), so that window of this process is
// found and the ids tried in turn: an icon that is not there refuses the change. No icon (the checks) — no notification.
public static partial class Notifier
{
    public static bool Show(string title, string text)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ShowWindows(title, text);
            if (OperatingSystem.IsLinux()) return ShowLinux(title, text);
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
        return false;
    }

    private static bool ShowLinux(string title, string text)
    {
        var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "--app-name=Minter Wallet", "--icon=dialog-information", title, text }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        return process != null;
    }

    private static bool ShowWindows(string title, string text)
    {
        var pid = (uint)Environment.ProcessId;
        var window = IntPtr.Zero;
        while ((window = FindWindowExW(IntPtr.Zero, window, null, null)) != IntPtr.Zero)
        {
            if (GetWindowThreadProcessId(window, out var owner) == 0 || owner != pid) continue;
            var name = new StringBuilder(256);
            if (GetClassNameW(window, name, name.Capacity) == 0 || !name.ToString().StartsWith("AvaloniaMessageWindow", StringComparison.Ordinal)) continue;
            for (uint id = 1; id <= 4; id++)
            {
                var data = new NotifyIconData
                {
                    Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = id, Flags = NifInfo,
                    Info = Cut(text, 255), InfoTitle = Cut(title, 63), InfoFlags = NiifInfo | NiifRespectQuietTime, Tip = "",
                };
                if (Shell_NotifyIconW(NimModify, ref data)) return true;
            }
        }
        return false;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private const uint NimModify = 1, NifInfo = 0x10, NiifInfo = 0x1, NiifRespectQuietTime = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);
}
