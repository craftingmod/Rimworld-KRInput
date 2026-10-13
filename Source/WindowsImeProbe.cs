using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RimWorldKRInput
{
    internal static class WindowsImeProbe
    {
        private const int WhGetMessage = 3;
        private const int WhCallWndProc = 4;
        private const uint GcsCompStr = 0x8;
        private const uint GcsResultStr = 0x800;
        private const int MaxStringBytes = 16384;
        private const int MaxQueuedLogs = 256;
        private static readonly ConcurrentQueue<string> Logs = new ConcurrentQueue<string>();
        private static readonly object CompositionLock = new object();
        private static string _composition = "";
        private static long _session;
        private static bool _composing;
        // Keep delegates alive for the entire lifetime of native hooks, including unhook failures.
        private static readonly HookProc QueueHook = OnGetMessage;
        private static readonly HookProc WindowHook = OnCallWndProc;
        private static IntPtr _queueHandle;
        private static IntPtr _windowHandle;
        private static uint _thread;
        private static IntPtr _window;
        private static int _queued;
        private static int _dropped;
        private static int _callbackError;
        private static long _nextAttempt;
        private static bool _started;
        private static bool _disabled;

        internal static void Poll()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || _disabled)
                return;
            try
            {
                if (!_started)
                {
                    _started = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, __) => Stop();
                    ImeProbe.Write("Win32 probe: waiting for a foreground window owned by this process.");
                }
                if (Environment.TickCount64 >= _nextAttempt)
                {
                    _nextAttempt = Environment.TickCount64 + 1000;
                    var window = GetForegroundWindow();
                    uint thread = GetWindowThreadProcessId(window, out uint process);
                    if (thread != 0 && process == GetCurrentProcessId())
                        AttachWindow(window);
                }
                Drain();
            }
            catch (Exception exception)
            {
                _disabled = true;
                Stop();
                ImeProbe.Write($"Win32 probe disabled after error: {exception}");
            }
        }

        internal static void AttachWindow(IntPtr window)
        {
            uint thread = GetWindowThreadProcessId(window, out uint process);
            if (thread == 0 || process != GetCurrentProcessId())
                throw new InvalidOperationException("Win32 probe requires a window owned by this process.");
            if (thread == _thread && (_queueHandle != IntPtr.Zero || _windowHandle != IntPtr.Zero))
            {
                if (window != _window)
                {
                    _window = window;
                    ImeProbe.Write($"Win32 probe window changed: hwnd=0x{window.ToInt64():X} thread={thread}");
                }
                return;
            }
            Stop();
            if (_queueHandle != IntPtr.Zero || _windowHandle != IntPtr.Zero)
                throw new InvalidOperationException("Previous Win32 hooks could not be removed.");
            _thread = thread;
            _window = window;
            _queueHandle = SetWindowsHookExW(WhGetMessage, QueueHook, IntPtr.Zero, thread);
            int queueError = _queueHandle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            _windowHandle = SetWindowsHookExW(WhCallWndProc, WindowHook, IntPtr.Zero, thread);
            int windowError = _windowHandle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            ImeProbe.Write($"Win32 hooks: hwnd=0x{window.ToInt64():X} thread={thread} "
                + $"GETMESSAGE={(_queueHandle != IntPtr.Zero ? "installed" : "failed")} error={queueError} "
                + $"CALLWNDPROC={(_windowHandle != IntPtr.Zero ? "installed" : "failed")} error={windowError}");
            if (_queueHandle == IntPtr.Zero && _windowHandle == IntPtr.Zero)
                throw new InvalidOperationException("Both Win32 hooks failed to install.");
        }

        internal static void Stop()
        {
            if (_queueHandle != IntPtr.Zero && UnhookWindowsHookEx(_queueHandle))
                _queueHandle = IntPtr.Zero;
            if (_windowHandle != IntPtr.Zero && UnhookWindowsHookEx(_windowHandle))
                _windowHandle = IntPtr.Zero;
            _thread = 0;
            SetComposition(0x010E, 0, null);
        }

        internal static void GetComposition(out string text, out long session)
        {
            lock (CompositionLock)
            {
                text = _composition;
                session = _session;
            }
        }

        internal static void SetComposition(uint message, uint flags, string preedit)
        {
            lock (CompositionLock)
            {
                if (message == 0x010D)
                {
                    if (!_composing)
                    {
                        _session++;
                        _composing = true;
                        _composition = "";
                    }
                }
                else if (message == 0x010F)
                {
                    if ((flags & GcsResultStr) != 0)
                    {
                        _composition = "";
                        _composing = false;
                    }
                    if ((flags & GcsCompStr) != 0)
                    {
                        if (!_composing) _session++;
                        _composing = true;
                        _composition = preedit ?? "";
                    }
                    if (flags == 0)
                    {
                        _composition = "";
                        _composing = false;
                    }
                }
                else
                {
                    _composition = "";
                    _composing = false;
                }
            }
        }

        internal static void Drain()
        {
            while (Logs.TryDequeue(out string message))
            {
                Interlocked.Decrement(ref _queued);
                ImeProbe.Write(message);
            }
            int dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped != 0)
                ImeProbe.Write($"Win32 probe: dropped {dropped} events because the diagnostic queue was full.");
        }

        private static void Enqueue(string message)
        {
            if (Interlocked.Increment(ref _queued) > MaxQueuedLogs)
            {
                Interlocked.Decrement(ref _queued);
                Interlocked.Increment(ref _dropped);
                return;
            }
            Logs.Enqueue(message);
        }

        private static IntPtr OnGetMessage(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // PM_NOREMOVE may expose the same queued message repeatedly.
                if (code >= 0 && wParam == new IntPtr(1))
                {
                    var message = Marshal.PtrToStructure<Msg>(lParam);
                    Observe("GETMESSAGE", message.Window, message.Message, message.WParam, message.LParam);
                }
            }
            catch (Exception exception) { CallbackError(exception); }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private static IntPtr OnCallWndProc(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0)
                {
                    var message = Marshal.PtrToStructure<CwpStruct>(lParam);
                    Observe("CALLWNDPROC", message.Window, message.Message, message.WParam, message.LParam);
                }
            }
            catch (Exception exception) { CallbackError(exception); }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private static void CallbackError(Exception exception)
        {
            if (Interlocked.Exchange(ref _callbackError, 1) == 0)
                Enqueue($"Win32 hook callback error: {exception}");
        }

        private static void Observe(string source, IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
        {
            string name;
            switch (message)
            {
                case 0x0007: name = "WM_SETFOCUS"; break;
                case 0x0008: name = "WM_KILLFOCUS"; break;
                case 0x0051: name = "WM_INPUTLANGCHANGE"; break;
                case 0x0102: name = "WM_CHAR"; break;
                case 0x010D: name = "WM_IME_STARTCOMPOSITION"; break;
                case 0x010E: name = "WM_IME_ENDCOMPOSITION"; break;
                case 0x010F: name = "WM_IME_COMPOSITION"; break;
                case 0x0281: name = "WM_IME_SETCONTEXT"; break;
                case 0x0286: name = "WM_IME_CHAR"; break;
                default: return;
            }
            if (GetWindowThreadProcessId(window, out uint process) == 0 || process != GetCurrentProcessId())
                return;
            if (window != _window)
                return;
            long time = Environment.TickCount64;
            string data = "";
            if (message == 0x010D || message == 0x010E || message == 0x0008 || message == 0x0051
                || (message == 0x0281 && wParam == UIntPtr.Zero))
                SetComposition(message, 0, null);
            if (message == 0x010F)
            {
                uint flags = unchecked((uint)lParam.ToInt64());
                string preedit = null;
                IntPtr context = ImmGetContext(window);
                if (context == IntPtr.Zero)
                    data = " <no HIMC>";
                else
                {
                    try
                    {
                        if ((flags & GcsCompStr) != 0) data += " preedit=" + ReadString(context, GcsCompStr, out preedit);
                        if ((flags & GcsResultStr) != 0) data += " result=" + ReadString(context, GcsResultStr, out _);
                    }
                    finally
                    {
                        if (!ImmReleaseContext(window, context))
                            Enqueue("Win32 probe: ImmReleaseContext failed.");
                    }
                }
                SetComposition(message, flags, preedit);
            }
            Enqueue($"Win32 {source}: {name} hwnd=0x{window.ToInt64():X} "
                + $"tick={time} thread={GetCurrentThreadId()} wParam=0x{wParam.ToUInt64():X} "
                + $"lParam=0x{lParam.ToInt64():X}{data}");
        }

        private static string ReadString(IntPtr context, uint index, out string value)
        {
            value = null;
            int length = ImmGetCompositionStringW(context, index, null, 0);
            if (length < 0) return $"<IMM error {length}>";
            if (length > MaxStringBytes || (length & 1) != 0) return $"<invalid byte length {length}>";
            if (length == 0)
            {
                value = "";
                return ImeProbe.Quote(value);
            }
            var buffer = new byte[length];
            int copied = ImmGetCompositionStringW(context, index, buffer, (uint)buffer.Length);
            if (copied < 0) return $"<IMM error {copied}>";
            if (copied > buffer.Length || (copied & 1) != 0) return $"<invalid copied length {copied}>";
            value = Encoding.Unicode.GetString(buffer, 0, copied);
            return ImeProbe.Quote(value);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct CwpStruct
        {
            public IntPtr LParam;
            public UIntPtr WParam;
            public uint Message;
            public IntPtr Window;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
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

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr window);
        [DllImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ImmReleaseContext(IntPtr window, IntPtr context);
        [DllImport("imm32.dll")] private static extern int ImmGetCompositionStringW(IntPtr context, uint index, [Out] byte[] buffer, uint length);
    }
}
