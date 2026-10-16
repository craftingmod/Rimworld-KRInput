using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityStubs.Gui;
using Verse;
using Waylith;

namespace RimWorldKRInput
{
    internal static class ImePatches
    {
        private static readonly Harmony Harmony = new("rimworld.krinput.ime");
        private static AccessTools.FieldRef<int> Caret, Anchor;
        private static AccessTools.FieldRef<float> ScrollX, ScrollY;
        private static MethodInfo TextRect, WrapWidth;
        private static FieldInfo ReadOnlyId;
        private static PropertyInfo CapturesText;
        private static FieldInfo DispatchInstance;
        private static MethodInfo EnsureCharHook;
        private static MethodInfo RunKeyPass;
        private static int fieldDepth, control;
        private static bool secure;
        private static readonly List<Rect> CompositionRects = new();

        internal static void Install()
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                Caret = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(GUI), "_caretIndex"));
                Anchor = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(GUI), "_selectAnchor"));
                ScrollX = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(GUI), "_textScrollX"));
                ScrollY = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(GUI), "_textScrollY"));
                TextRect = Require(typeof(GUI), "TextFieldTextRect", typeof(Rect), typeof(bool), typeof(GUIStyle));
                WrapWidth = Require(typeof(GUI), "TextAreaWrapWidth", typeof(Rect), typeof(GUIStyle));
                var textFocus = AccessTools.TypeByName("UnityStubs.Internal.TextFocus");
                ReadOnlyId = AccessTools.Field(textFocus, "ReadOnlyId");
                CapturesText = AccessTools.Property(textFocus, "CapturesText");
                var bootstrap = AccessTools.TypeByName("RimWorldWaylith.Bootstrap");
                var dispatch = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImguiDispatch");
                DispatchInstance = AccessTools.Field(bootstrap, "_imguiDispatch");
                EnsureCharHook = Require(dispatch, "EnsureCharHook");
                RunKeyPass = Require(dispatch, "RunKeyPass", typeof(KeyCode), typeof(char), typeof(Vector2), typeof(EventModifiers), typeof(EventType));
                var targets = new[] {
                    (Require(bootstrap, "OnPrePollEvents", typeof(EngineApplication)), null, "PrePoll", null),
                    (Require(bootstrap, "OnAppShuttingDown"), "Shutdown", null, null),
                    (Require(typeof(GUI), "DoTextField", typeof(Rect), typeof(int), typeof(GUIContent), typeof(bool), typeof(int), typeof(GUIStyle), typeof(string), typeof(char)), "SecurePrefix", null, "SecureFinalizer"),
                    (Require(typeof(GUI), "PasswordField", typeof(Rect), typeof(string), typeof(char), typeof(GUIStyle)), "PasswordPrefix", null, "SecureFinalizer"),
                    (Require(typeof(GUI), "TextFieldImpl", typeof(Rect), typeof(string), typeof(GUIStyle), typeof(bool)), "FieldPrefix", null, "FieldFinalizer"),
                    (Require(typeof(GUI), "TrackControlWindow", typeof(int)), null, "Track", null),
                    (Require(typeof(GUI), "DrawTextFieldChrome", typeof(Rect), typeof(string), typeof(int), typeof(bool), typeof(bool), typeof(GUIStyle), typeof(IGuiBackend)), "DrawPrefix", "DrawPostfix", "DrawFinalizer"),
                    (Require(typeof(GUI), "ApplyTextKey", typeof(string), typeof(Event), typeof(bool)), "EditPrefix", "EditPostfix", null),
                    (Require(dispatch, "DispatchKeyboard", typeof(Vector2), typeof(EventModifiers)), "DispatchCommits", null, null),
                    (Require(typeof(UnityEngine.InputSystem.InputSystem), "QueueTextInput", typeof(char)), "QueueText", null, null),
                    (Require(dispatch, "OnTextInput", typeof(char)), "ReceiveText", null, null),
                    (RunKeyPass, "KeyPrefix", null, null),
                    (Require(typeof(Input), "get_compositionString"), "CompositionGetter", null, null),
                    (Require(typeof(Input), "set_compositionCursorPos", typeof(Vector2)), "CursorSetter", null, null),
                    (Require(typeof(Input), "set_imeCompositionMode", typeof(IMECompositionMode)), "ModeSetter", null, null)
                };
                if (TextRect == null || WrapWidth == null || ReadOnlyId == null || CapturesText == null || DispatchInstance == null)
                    throw new MissingMemberException("GUI layout/text focus contract changed");
                foreach (var (target, prefix, postfix, finalizer) in targets)
                    Harmony.Patch(target, Hook(prefix), Hook(postfix), finalizer: Hook(finalizer));
                Log.Message("[KRInput] IME Harmony patches installed (15 hooks).");
            }
            catch (Exception error)
            {
                Harmony.UnpatchAll(Harmony.Id);
                WindowsImeBridge.Fail(error);
            }
        }

        private static MethodInfo Require(Type type, string name, params Type[] args) =>
            AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type?.FullName, name);
        private static HarmonyMethod Hook(string name) => name == null ? null : new HarmonyMethod(typeof(ImePatches), name);
        private static void PrePoll(EngineApplication __0)
        {
            if (WindowsImeBridge.Disabled) return;
            try
            {
                var dispatch = DispatchInstance.GetValue(null);
                if (dispatch != null) EnsureCharHook.Invoke(dispatch, null);
                WindowsImeBridge.PrePoll(__0);
            }
            catch (Exception error) { WindowsImeBridge.Fail(error); }
        }
        private static void Shutdown() => WindowsImeBridge.Stop();
        private static void SecurePrefix(string __6, char __7, out bool __state)
        { __state = secure; secure = secure || __6 != null || __7 != '\0'; }
        private static void PasswordPrefix(out bool __state) { __state = secure; secure = true; }
        private static void SecureFinalizer(bool __state) => secure = __state;
        private static void FieldPrefix(out (int Depth, int Control) __state)
        { __state = (fieldDepth, control); fieldDepth++; control = 0; }
        private static void FieldFinalizer((int Depth, int Control) __state)
        { fieldDepth = __state.Depth; control = __state.Control; }
        private static void Track(int __0) { if (fieldDepth > 0) control = __0; }

        private sealed class DrawState
        {
            internal int Caret, Anchor, Start, End;
            internal float X, Y;
            internal string Text;
        }

        private static void DrawPrefix(Rect __0, ref string __1, bool __3, out DrawState __state)
        {
            __state = null;
            if (WindowsImeBridge.Disabled || !__3 || fieldDepth == 0 || control == 0 || ImeState.Delivering != null) return;
            if (!GUI.enabled || secure || (int)ReadOnlyId.GetValue(null) == control || !(bool)CapturesText.GetValue(null))
            {
                WindowsImeBridge.Focus(0);
                return;
            }
            WindowsImeBridge.Focus(control);
            ImeState.Caret = Caret();
            ImeState.Anchor = Anchor();
            ImeState.FieldText = __1;
            if (ImeState.Composing && ImeState.CompositionOwner == control && ImeState.Commits.Count == 0 && ImeState.TextSources.Count == 0 && ImeState.CompositionText != __1)
                WindowsImeBridge.Cancel();
            if (ImeState.CompositionOwner != control || ImeState.Composition.Length == 0) return;
            __state = new DrawState { Caret = Caret(), Anchor = Anchor(), X = ScrollX(), Y = ScrollY(), Start = ImeState.Start,
                End = ImeState.Start + ImeState.Composition.Length };
            __1 = ImeState.Preview(__1, out int caret);
            __state.Text = __1;
            Caret() = Anchor() = caret;
        }

        private static void DrawPostfix(Rect __0, string __1, int __2, bool __3, bool __4, GUIStyle __5, IGuiBackend __6, DrawState __state)
        {
            if (!__3 || __6 == null || fieldDepth == 0 || control != ImeState.Owner) return;
            Rect area = (Rect)TextRect.Invoke(null, new object[] { __0, __4, __5 });
            float wrap = __4 ? (float)WrapWidth.Invoke(null, new object[] { area, __5 }) : -1f;
            Vector2 caret = __6.TextCursorPos(__2, 0f, FontStyle.Normal, __1, wrap, Math.Clamp(Caret(), 0, __1.Length), out float lineHeight, richText: true);
            float offset = 0f;
            if (!__4)
            {
                float width = __6.TextCursorPos(__2, 0f, FontStyle.Normal, __1, -1f, __1.Length, out _, richText: true).x;
                int align = (int)(__5 ?? GUI.skin.textField).alignment % 3;
                offset = Math.Max(0f, area.width - width) * (align == 1 ? 0.5f : align == 2 ? 1f : 0f);
                caret.y = __0.y - area.y + 3f;
                lineHeight = Math.Max(2f, __0.height - 6f);
            }
            float x = area.x + offset - ScrollX();
            float y = area.y - (__4 ? ScrollY() : 0f);
            WindowsImeBridge.Candidate(__6.GuiToScreenPoint(new Vector2(x + caret.x, y + caret.y + lineHeight)));
            if (__state == null) return;
            CompositionRects.Clear();
            __6.TextSelectionRects(__2, 0f, FontStyle.Normal, __state.Text, wrap, __state.Start, __state.End, CompositionRects, richText: true);
            __6.PushClipRect(area);
            try
            {
                foreach (Rect range in CompositionRects)
                    __6.FillRect(new Rect(x + range.x, y + range.y + (__4 ? range.height : lineHeight) - 1f, range.width, 1f), GUI.contentColor);
            }
            finally { __6.PopClipRect(); }
        }

        private static void DrawFinalizer(DrawState __state, Exception __exception)
        {
            if (__state != null)
            {
                Caret() = __state.Caret;
                Anchor() = __state.Anchor;
                ScrollX() = __state.X;
                ScrollY() = __state.Y;
            }
            if (__exception != null) WindowsImeBridge.Fail(__exception);
        }

        private static bool EditPrefix(string __0, Event __1, ref string __result)
        {
            var commit = ImeState.Delivering;
            if (commit == null) return true;
            if (control != commit.Owner) { __result = __0; return false; }
            if (ImeState.FirstCharacter)
            {
                Caret() = Math.Clamp(commit.Caret, 0, __0.Length);
                Anchor() = Math.Clamp(commit.Anchor, 0, __0.Length);
                ImeState.FirstCharacter = false;
            }
            return true;
        }

        private static void EditPostfix(string __result)
        {
            if (ImeState.Delivering != null && control == ImeState.CompositionOwner)
            {
                ImeState.Start = ImeState.End = Caret();
                ImeState.Caret = Caret();
                ImeState.Anchor = Anchor();
                ImeState.CompositionText = __result;
            }
        }

        private static void QueueText(char __0)
        {
            if (WindowsImeBridge.Disabled) return;
            var native = ImeState.NativeCommit;
            ImeState.TextSources.Enqueue(new ImeState.Commit { Owner = native?.Owner ?? ImeState.Owner,
                Text = __0.ToString(), Caret = native?.Caret ?? 0, Anchor = native?.Anchor ?? 0,
                RestoreSelection = native != null && native.RestoreSelection });
            if (native == null && ImeState.Owner != 0 && !char.IsControl(__0))
            {
                ImeState.Caret = ImeState.Anchor = Math.Min(ImeState.Caret, ImeState.Anchor) + 1;
                if (ImeState.CompositionOwner == ImeState.Owner) ImeState.Start = ImeState.End = ImeState.Caret;
            }
        }

        private static bool ReceiveText(char __0)
        {
            if (WindowsImeBridge.Disabled) return true;
            if (ImeState.TextSources.Count == 0) return true;
            var source = ImeState.TextSources.Dequeue();
            if (source.Owner == 0) return true;
            ImeState.Commits.Enqueue(source);
            return false;
        }

        private static void DispatchCommits(object __instance, Vector2 __0, EventModifiers __1)
        {
            try
            {
                while (ImeState.Commits.Count > 0)
                {
                    var commit = ImeState.Commits.Dequeue();
                    if (commit.Owner == 0) continue;
                    int focus = GUIUtility.keyboardControl;
                    try
                    {
                        ImeState.Delivering = commit;
                        ImeState.FirstCharacter = commit.RestoreSelection;
                        GUIUtility.keyboardControl = commit.Owner;
                        foreach (char character in commit.Text)
                            RunKeyPass.Invoke(__instance, new object[] { KeyCode.None, character, __0, __1, EventType.keyDown });
                    }
                    finally { GUIUtility.keyboardControl = focus; ImeState.Delivering = null; }
                }
            }
            catch (Exception error) { WindowsImeBridge.Fail(error); }
        }

        private static bool KeyPrefix(KeyCode __0, char __1, EventType __4) =>
            __1 != '\0' || __4 != EventType.keyDown || !ImeState.ConsumedKeys.Contains(__0);
        private static bool CompositionGetter(ref string __result) { __result = ImeState.Composition; return false; }
        private static bool CursorSetter(Vector2 __0) { WindowsImeBridge.Candidate(__0); return false; }
        private static bool ModeSetter(IMECompositionMode __0) { WindowsImeBridge.Mode = __0; return false; }
    }
}
