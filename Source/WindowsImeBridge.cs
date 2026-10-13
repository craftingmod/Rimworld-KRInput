using System;
using System.Runtime.InteropServices;
using System.Text;
using Brutal.GlfwApi;
using UnityEngine;
using Verse;
using Waylith;

namespace RimWorldKRInput
{
    internal static class WindowsImeBridge
    {
        private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
        private static readonly SubclassProc Callback = WindowProc;
        private static nint window, borrowedContext;
        private static bool detached, disabled, readable, cancelling;
        internal static IMECompositionMode Mode = IMECompositionMode.Auto;
        internal static bool Active => window != 0 && !detached && readable && !disabled;
        internal static bool Disabled => disabled;

        internal static void PrePoll(EngineApplication app)
        {
            if (disabled || !OperatingSystem.IsWindows() || app.Headless || app.Window == null) return;
            try
            {
                nint hwnd = Glfw.PInvoke.GetWin32Window(app.Window);
                if (window != hwnd)
                {
                    Stop();
                    if (!Attach(hwnd)) return;
                }
                ImeState.Frame++;
                ImeState.ConsumedKeys.Clear();
                ImeState.PendingControl = KeyCode.None;
                ImeState.TextSources.Clear();
                if (ImeState.SeenFrame != ImeState.Frame - 1 || ImeState.Owner != GUIUtility.keyboardControl)
                {
                    Cancel();
                    ImeState.Owner = 0;
                }
                SetEnabled(ImeState.Owner != 0 && app.Window.IsFocused && Mode != IMECompositionMode.Off);
            }
            catch (Exception error) { Fail(error); }
        }

        internal static bool Attach(nint hwnd)
        {
            if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out _) != GetCurrentThreadId()) return false;
            if (!SetWindowSubclass(hwnd, Callback, 1, 0)) throw new InvalidOperationException("SetWindowSubclass failed");
            window = hwnd;
            nint context = ImmGetContext(window);
            try { readable = context != 0 && ReadString(context, 8) != null; }
            finally { if (context != 0) ImmReleaseContext(window, context); }
            if (!readable) throw new InvalidOperationException("IMM composition is unavailable");
            Log.Message("[KRInput] Windows IME bridge attached.");
            return true;
        }

        internal static void Focus(int owner)
        {
            if (ImeState.Delivering != null) return;
            if (ImeState.Owner != owner)
            {
                Cancel();
                ImeState.Owner = owner;
            }
            ImeState.SeenFrame = ImeState.Frame;
            if (window != 0 && GetForegroundWindow() == window)
                SetEnabled(owner != 0 && Mode != IMECompositionMode.Off);
        }

        private static void SetEnabled(bool enabled)
        {
            if (window == 0 || disabled) return;
            if (enabled && detached)
            {
                ImmAssociateContext(window, borrowedContext);
                detached = false;
                borrowedContext = 0;
            }
            else if (!enabled && !detached)
            {
                Cancel();
                borrowedContext = ImmAssociateContext(window, 0);
                detached = true;
            }
        }

        internal static void Cancel()
        {
            ImeState.PendingControl = KeyCode.None;
            if (window != 0 && !detached && ImeState.Composing)
            {
                nint context = ImmGetContext(window);
                cancelling = true;
                try { if (context != 0) ImmNotifyIME(context, 0x15, 4, 0); }
                finally
                {
                    cancelling = false;
                    if (context != 0) ImmReleaseContext(window, context);
                }
            }
            ImeState.ClearComposition();
        }

        internal static void Stop()
        {
            if (window != 0)
            {
                Cancel();
                if (detached) ImmAssociateContext(window, borrowedContext);
                RemoveWindowSubclass(window, Callback, 1);
            }
            window = borrowedContext = 0;
            detached = readable = false;
            ImeState.Owner = 0;
            ImeState.Commits.Clear();
            ImeState.Results.Clear();
            ImeState.TextSources.Clear();
            ImeState.ConsumedKeys.Clear();
            ImeState.PendingControl = KeyCode.None;
            ImeState.ClearComposition();
        }

        internal static void Fail(Exception error)
        {
            Stop();
            disabled = true;
            Log.Warning("[KRInput] IME bridge disabled; original input restored: " + error.GetType().Name);
        }

        private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
        {
            try
            {
                if (message == 0x82) Stop(); // WM_NCDESTROY, on the window's own thread.
                else if (message == 8)
                {
                    Cancel();
                    ImeState.Owner = 0;
                    SetEnabled(false);
                }
                else if (Active)
                {
                    if (message == 0x100 || message == 0x104)
                    {
                        int vk = (int)wParam;
                        bool processed = vk == 0xe5;
                        // VK_PROCESSKEY has already passed TranslateMessage; use its scan code.
                        if (processed) vk = (int)MapVirtualKeyW((uint)((lParam.ToInt64() >> 16) & 255), 1);
                        KeyCode key = ImeState.ControlKey(vk);
                        ImeState.PendingControl = ImeState.Composing ? key : KeyCode.None;
                        if (processed) ImeState.ConsumeControl(key);
                    }
                    else if (message == 0x281)
                        lParam = (nint)(lParam.ToInt64() & ~0x80000000L);
                    else if (message == 0x10d)
                    {
                        ImeState.ConsumeControl(ImeState.PendingControl);
                        ImeState.Begin();
                        return 0;
                    }
                    else if (message == 0x10f)
                    {
                        if (cancelling) return 0;
                        ImeState.ConsumeControl(ImeState.PendingControl);
                        nint context = ImmGetContext(hwnd);
                        if (context == 0) throw new InvalidOperationException("Missing IME context");
                        try
                        {
                            long flags = lParam.ToInt64();
                            string result = (flags & 0x800) != 0 ? ReadString(context, 0x800) : "";
                            string composition = flags == 0 ? "" : (flags & 8) != 0 ? ReadString(context, 8) : ImeState.Composition;
                            if (result == null || composition == null) throw new InvalidOperationException("Composition read failed");
                            int cursor = (flags & 0x80) != 0 ? ImmGetCompositionStringW(context, 0x80, null, 0) : ImeState.Cursor;
                            if (cursor < 0) throw new InvalidOperationException("Composition cursor read failed");
                            if ((flags & 0x800) != 0 && (flags & 8) == 0) composition = "";
                            ImeState.Update(result, composition, cursor);
                            QueueResults();
                        }
                        finally { ImmReleaseContext(hwnd, context); }
                        // We own RESULTSTR; DefSubclassProc would generate a second WM_CHAR commit.
                        return 0;
                    }
                    else if (message == 0x10e)
                    {
                        ImeState.ConsumeControl(ImeState.PendingControl);
                        ImeState.ClearComposition();
                        return 0;
                    }
                    else if (message == 0x286) return 0; // WM_IME_CHAR: RESULTSTR was already queued.
                }
            }
            catch (Exception error) { Fail(error); }
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        internal static void QueueResults()
        {
            while (ImeState.Results.Count > 0)
            {
                var commit = ImeState.Results.Dequeue();
                try
                {
                    ImeState.NativeCommit = commit;
                    foreach (char character in commit.Text)
                    {
                        UnityEngine.InputSystem.InputSystem.QueueTextInput(character);
                        commit.RestoreSelection = false;
                    }
                }
                finally { ImeState.NativeCommit = null; }
            }
        }

        private static string ReadString(nint context, uint index)
        {
            int bytes = ImmGetCompositionStringW(context, index, null, 0);
            if (bytes == -1 && index == 8) return ""; // IMM_ERROR_NODATA is an empty preedit, not an API failure.
            if (bytes < 0 || (bytes & 1) != 0 || bytes > 1024 * 1024) return null;
            if (bytes == 0) return "";
            var buffer = new byte[bytes];
            int read = ImmGetCompositionStringW(context, index, buffer, (uint)bytes);
            return read == bytes ? Encoding.Unicode.GetString(buffer) : null;
        }

        internal static void Candidate(Vector2 framebufferPoint)
        {
            if (!Active) return;
            var app = EngineApplication.Instance;
            if (app == null || app.Window == null) return;
            var size = app.Window.FramebufferSize;
            if (size.X <= 0 || size.Y <= 0 || !GetClientRect(window, out NativeRect client)) return;
            var form = new CandidateForm { Style = 0x40, Position = new Point {
                X = (int)Math.Round(framebufferPoint.x * client.Right / size.X),
                Y = (int)Math.Round(framebufferPoint.y * client.Bottom / size.Y) } };
            form.Area = new NativeRect { Left = form.Position.X, Top = form.Position.Y,
                Right = form.Position.X + 1, Bottom = form.Position.Y + 1 };
            nint context = ImmGetContext(window);
            try { if (context != 0) ImmSetCandidateWindow(context, ref form); }
            finally { if (context != 0) ImmReleaseContext(window, context); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct CandidateForm { public uint Index, Style; public Point Position; public NativeRect Area; }
        [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
        [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
        [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint type);
        [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint hwnd);
        [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(nint hwnd, nint context);
        [DllImport("imm32.dll")] private static extern nint ImmAssociateContext(nint hwnd, nint context);
        [DllImport("imm32.dll")] private static extern int ImmGetCompositionStringW(nint context, uint index, byte[] buffer, uint bytes);
        [DllImport("imm32.dll")] private static extern bool ImmNotifyIME(nint context, uint action, uint index, uint value);
        [DllImport("imm32.dll")] private static extern bool ImmSetCandidateWindow(nint context, ref CandidateForm form);
    }
}
