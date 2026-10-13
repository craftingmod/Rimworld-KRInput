using System;
using System.Linq;
using System.Runtime.InteropServices;
using RimWorldKRInput;

static class NativeSmoke
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    internal static void Run()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("SKIP: real Win32 hook check requires Windows.");
            return;
        }
        IntPtr window = CreateWindowExW(0, "STATIC", "KRInput smoke", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Check(window != IntPtr.Zero, $"Hidden test window creation failed: {Marshal.GetLastWin32Error()}");
        try
        {
            try { WindowsImeProbe.AttachWindow(IntPtr.Zero); throw new Exception("Invalid HWND accepted"); }
            catch (InvalidOperationException) { }
            IntPtr textLength = SendMessageW(window, 0xE, UIntPtr.Zero, IntPtr.Zero);
            WindowsImeProbe.AttachWindow(window);
            WindowsImeProbe.ProbeImeStatus("test", window);
            WindowsImeProbe.Drain();
            Check(Verse.Log.Messages.Any(line => line.Contains("IME status:") && line.Contains("target=0x")
                && line.Contains("active=0x") && line.Contains("foreground=0x")
                && line.Contains("himc=0x") && line.Contains("open=")), "IMM status snapshot missing");
            int statusLogs = Verse.Log.Messages.Count(line => line.Contains("IME status:"));
            WindowsImeProbe.ProbeImeStatus("test repeat", window);
            WindowsImeProbe.Drain();
            Check(Verse.Log.Messages.Count(line => line.Contains("IME status:")) == statusLogs,
                "Unchanged IMM state must not spam logs");
            WindowsImeProbe.ProbeImeStatus("ResetToOff/before", window, force: true);
            WindowsImeProbe.ProbeImeStatus("ResetToOff/after", window, force: true);
            WindowsImeProbe.Drain();
            Check(Verse.Log.Messages.Count(line => line.Contains("IME status:")) == statusLogs + 2,
                "Manual before/after snapshots must be visible even if state is unchanged");
            Check(Verse.Log.Messages.Any(line => line.Contains("GETMESSAGE=installed") && line.Contains("CALLWNDPROC=installed")),
                "Both native thread hooks must install");
            Check(SendMessageW(window, 0xE, UIntPtr.Zero, IntPtr.Zero) == textLength, "Window procedure return value changed");
            SendMessageW(window, 0x0102, new UIntPtr('.'), IntPtr.Zero);
            WindowsImeProbe.Drain();
            Check(Verse.Log.Messages.Any(line => line.Contains("Win32 CALLWNDPROC: WM_CHAR") && line.Contains("wParam=0x2E")),
                "Sent WM_CHAR not observed");
            SendMessageW(window, 0x010F, UIntPtr.Zero, new IntPtr(0x8));
            WindowsImeProbe.Drain();
            Check(Verse.Log.Messages.Any(line => line.Contains("WM_IME_COMPOSITION") && line.Contains("lParam=0x8")),
                "Synthetic composition message not observed");
            Check(PostMessageW(window, 0x0102, new UIntPtr('A'), IntPtr.Zero), "Posting WM_CHAR failed");
            Check(PeekMessageW(out Msg message, window, 0x0102, 0x0102, 0), "Peek without removal failed");
            WindowsImeProbe.Drain();
            Check(!Verse.Log.Messages.Any(line => line.Contains("Win32 GETMESSAGE: WM_CHAR")), "PM_NOREMOVE must not duplicate events");
            Check(PeekMessageW(out message, window, 0x0102, 0x0102, 1), "Queue removal failed");
            Check(message.WParam == new UIntPtr('A'), "Queued message was changed");
            WindowsImeProbe.Drain();
            Check(Verse.Log.Messages.Count(line => line.Contains("Win32 GETMESSAGE: WM_CHAR")) == 1, "Removed WM_CHAR must log exactly once");
            for (int i = 0; i < 300; i++) SendMessageW(window, 0x0102, new UIntPtr('B'), IntPtr.Zero);
            WindowsImeProbe.Drain();
            Check(Verse.Log.Messages.Any(line => line.Contains("dropped 44 events")), "Bounded queue overflow must be visible");
            WindowsImeProbe.Stop();
            int logs = Verse.Log.Messages.Count;
            SendMessageW(window, 0x0102, new UIntPtr('C'), IntPtr.Zero);
            WindowsImeProbe.Drain();
            Check(Verse.Log.Messages.Count == logs, "Removed hooks still observed messages");
            Check(SendMessageW(window, 0xE, UIntPtr.Zero, IntPtr.Zero) == textLength, "Window procedure changed after teardown");
            Console.WriteLine("PASS: real Win32 thread hooks on a hidden message-only window; sent/queued messages, unchanged forwarding, PM_NOREMOVE, queue cap and teardown. Synthetic IME message only; no real IME input.");
        }
        finally
        {
            WindowsImeProbe.Stop();
            DestroyWindow(window);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PostMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PeekMessageW(out Msg message, IntPtr window, uint minimum, uint maximum, uint remove);
}
