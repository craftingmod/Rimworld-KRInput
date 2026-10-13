using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldKRInput
{
    internal static class ImeProbe
    {
        private const string Prefix = "[KRInput IME probe]";
        private static long _sequence;
        [ThreadStatic] private static FieldTrace _activeField;
        [ThreadStatic] private static ValidationState _validation;
        private static int _lastControl;
        private static Rect _lastRect;
        private static string _lastText;
        private static int _lastCaret;
        private static int _lastAnchor;

        internal struct FieldTrace
        {
            internal string Text;
            internal int Caret;
            internal int Anchor;
            internal EventType? Event;
            internal KeyCode? Key;
            internal bool Focused;
            internal int Control;
            internal bool Multiline;
            internal bool HadPreedit;
        }

        internal struct ValidationState
        {
            internal bool Limited;
            internal int Maximum;
            internal Regex Validator;
            internal int Limit => Limited ? Maximum : int.MaxValue;
        }

        internal struct RenderState
        {
            internal bool Changed;
            internal int Caret;
            internal int Anchor;
            internal float Scroll;
        }

        internal static void Install()
        {
            var harmony = new Harmony("rimworld.krinput.imeprobe");
            Patch(harmony, "UnityEngine.InputSystem.Keyboard", "OnIMECompositionChanged", null,
                nameof(CompositionPrefix));
            Patch(harmony, "RimWorldWaylith.Imgui.ImguiDispatch", "OnTextInput", new[] { typeof(char) },
                nameof(TextInputPrefix));
            Patch(harmony, "UnityEngine.GUI", "TextFieldImpl",
                new[] { typeof(Rect), typeof(string), typeof(GUIStyle), typeof(bool) },
                nameof(FieldPrefix), nameof(FieldPostfix), nameof(FieldFinalizer), transpiler: nameof(FieldTranspiler));
            // TextFieldImpl's control ID is local; its renderer supplies the actual focus flag.
            Patch(harmony, "UnityEngine.GUI", "DrawTextFieldChrome", null, nameof(DrawPrefix),
                finalizer: nameof(DrawFinalizer));
            Patch(harmony, "Waylith.Input.GlfwInputSource", "OnChar", null, nameof(BackendCharPrefix));
            Patch(harmony, "RimWorldWaylith.Imgui.ImguiDispatch", "DispatchInput", null,
                nameof(InputFramePrefix));
            Patch(harmony, "UnityEngine.GUI", "ApplyTextKey", new[] { typeof(string), typeof(Event), typeof(bool) },
                nameof(TextKeyPrefix));
            Patch(harmony, "UnityEngine.GUI", "DoTextField",
                new[] { typeof(Rect), typeof(int), typeof(GUIContent), typeof(bool), typeof(int), typeof(GUIStyle), typeof(string), typeof(char) },
                nameof(LimitPrefix), finalizer: nameof(ValidationFinalizer));
            Patch(harmony, "Verse.Widgets", "TextField", new[] { typeof(Rect), typeof(string), typeof(int), typeof(Regex) },
                nameof(ValidatorPrefix), finalizer: nameof(ValidationFinalizer));
            Write("Installation finished: common single-line TextField composition return experiment. Check Patched/Skipped/Patch failed entries. Typed text is included in diagnostic logs.");
        }

        private static void Patch(Harmony harmony, string typeName, string methodName, Type[] arguments,
            string prefix, string postfix = null, string finalizer = null, string transpiler = null)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Write($"Skipped: type {typeName} is not loaded.");
                    return;
                }
                Write($"Target type: {type.AssemblyQualifiedName}");
                if (methodName == "OnIMECompositionChanged")
                {
                    foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                        .Where(member => member.Name.IndexOf("IME", StringComparison.OrdinalIgnoreCase) >= 0))
                        Write($"Public IME member: {member}");
                }
                var method = AccessTools.DeclaredMethod(type, methodName, arguments);
                if (method == null)
                {
                    Write($"Skipped: {typeName}.{methodName} was not found.");
                    return;
                }
                harmony.Patch(method, Hook(prefix), Hook(postfix), Hook(transpiler), Hook(finalizer));
                Write($"Patched: {typeName}.{method}");
            }
            catch (Exception exception)
            {
                Log.Warning($"{Prefix} Patch failed for {typeName}.{methodName}: {exception}");
            }
        }

        private static HarmonyMethod Hook(string name)
        {
            return name == null ? null : new HarmonyMethod(typeof(ImeProbe), name);
        }

        private static void CompositionPrefix(object __0)
        {
            var characters = __0 as IEnumerable<char>;
            Write(characters == null
                ? $"Composition callback: unreadable payload type={__0?.GetType().FullName}"
                : $"Composition callback: text={Quote(new string(characters.ToArray()))}");
        }

        private static void TextInputPrefix(char __0)
        {
            Write($"Commit dispatch: character={Quote(__0.ToString())} U+{(int)__0:X4}");
        }

        private static void BackendCharPrefix(uint __1)
        {
            Write($"GLFW commit: U+{__1:X4}");
        }

        private static void InputFramePrefix()
        {
            ImeField.BeginFrame();
            WindowsImeProbe.Poll();
            WindowsImeProbe.GetComposition(out string text, out long session);
            ImePreedit.Update(text, session, GUIUtility.keyboardControl);
        }

        private static void FieldPrefix(string __1, int ____caretIndex, int ____selectAnchor,
            out FieldTrace __state)
        {
            __state = _activeField;
            _activeField = new FieldTrace
            {
                Text = __1,
                Caret = ____caretIndex,
                Anchor = ____selectAnchor,
                Event = Event.current?.type,
                Key = Event.current?.keyCode
            };
            if (Event.current?.type == EventType.mouseDown
                || (Event.current?.type == EventType.keyDown && Event.current.keyCode == KeyCode.Escape))
                ImePreedit.Cancel();
        }

        private static IEnumerable<CodeInstruction> FieldTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var track = AccessTools.Method(typeof(GUI), "TrackControlWindow");
            int index = code.FindIndex(instruction => instruction.Calls(track));
            if (index < 1 || !code[index - 1].opcode.Name.StartsWith("ldloc", StringComparison.Ordinal))
                throw new InvalidOperationException("TextField control-ID IL pattern not found.");
            var loadControl = code[index - 1];
            code.InsertRange(index + 1, new[]
            {
                new CodeInstruction(loadControl.opcode, loadControl.operand),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Ldsflda, AccessTools.Field(typeof(GUI), "_caretIndex")),
                new CodeInstruction(OpCodes.Ldsflda, AccessTools.Field(typeof(GUI), "_selectAnchor")),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ImeProbe), nameof(PrepareField))),
                new CodeInstruction(OpCodes.Starg_S, (byte)1)
            });
            return code;
        }

        private static string PrepareField(int control, string text, bool multiline, ref int caret, ref int anchor)
        {
            _activeField.Control = control;
            _activeField.Multiline = multiline;
            return ImeField.Prepare(control, text, ref caret, ref anchor, out _activeField.HadPreedit);
        }

        private static bool TextKeyPrefix(string __0, Event __1, ref string __result)
        {
            if (_activeField.Multiline || __1.character != '\0'
                || (!_activeField.HadPreedit && !ImeField.WasComposing(_activeField.Control)
                    && ImePreedit.ForControl(_activeField.Control).Length == 0))
                return true;
            switch (__1.keyCode)
            {
                case KeyCode.Backspace:
                case KeyCode.Delete:
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.Home:
                case KeyCode.End:
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    __1.Use();
                    __result = __0;
                    return false;
                default: return true;
            }
        }

        private static void LimitPrefix(int __4, out ValidationState __state)
        {
            __state = _validation;
            if (__4 >= 0)
            {
                _validation.Maximum = Math.Min(_validation.Limit, __4);
                _validation.Limited = true;
            }
        }

        private static void ValidatorPrefix(int __2, Regex __3, out ValidationState __state)
        {
            __state = _validation;
            _validation.Maximum = Math.Min(_validation.Limit, __2);
            _validation.Limited = true;
            _validation.Validator = __3;
        }

        private static void ValidationFinalizer(ValidationState __state)
        {
            _validation = __state;
        }

        private static void DrawPrefix(ref string __1, bool __3, bool __4,
            ref int ____caretIndex, ref int ____selectAnchor, ref float ____textScrollX,
            out RenderState __state)
        {
            __state = default;
            _activeField.Focused = __3;
            if (!__3 || __4 || Event.current?.rawType != EventType.repaint)
                return;
            string preedit = ImePreedit.ForControl(GUIUtility.keyboardControl);
            if (preedit.Length == 0)
                return;
            if (!ImeField.Preview(__1, preedit, ____caretIndex, ____selectAnchor,
                _validation.Limit, _validation.Validator, out string display, out int caret, out int anchor))
                return;
            __state = new RenderState
            {
                Changed = true,
                Caret = ____caretIndex,
                Anchor = ____selectAnchor,
                Scroll = ____textScrollX
            };
            // The renderer temporarily uses display indices; Finish applies the same value afterward.
            __1 = display;
            ____caretIndex = caret;
            ____selectAnchor = anchor;
        }

        private static void DrawFinalizer(RenderState __state, ref int ____caretIndex,
            ref int ____selectAnchor, ref float ____textScrollX)
        {
            if (!__state.Changed)
                return;
            ____caretIndex = __state.Caret;
            ____selectAnchor = __state.Anchor;
            ____textScrollX = __state.Scroll;
        }

        private static void FieldPostfix(Rect __0, ref string __result, ref int ____caretIndex, ref int ____selectAnchor)
        {
            __result = ImeField.Finish(_activeField.Control, __result, _activeField.Focused, _activeField.Multiline,
                ref ____caretIndex, ref ____selectAnchor, _validation.Limit, _validation.Validator);
            if (__result != _activeField.Text)
                GUI.changed = true;
            if (!_activeField.Focused)
                return;
            int control = GUIUtility.keyboardControl;
            if (control == _lastControl && __0.Equals(_lastRect) && __result == _lastText
                && ____caretIndex == _lastCaret && ____selectAnchor == _lastAnchor
                && __result == _activeField.Text)
                return;
            Write($"Field: control={control} rect={__0} event={_activeField.Event} key={_activeField.Key} "
                + $"text={Quote(_activeField.Text)} -> {Quote(__result)} "
                + $"caret={_activeField.Caret}->{____caretIndex} selection={_activeField.Anchor}->{____selectAnchor}");
            _lastControl = control;
            _lastRect = __0;
            _lastText = __result;
            _lastCaret = ____caretIndex;
            _lastAnchor = ____selectAnchor;
        }

        private static void FieldFinalizer(FieldTrace __state)
        {
            _activeField = __state;
        }

        internal static string Quote(string text)
        {
            return text == null ? "<null>" : "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\0", "\\0") + "\"";
        }

        internal static void Write(string message)
        {
            Log.Message($"{Prefix} #{Interlocked.Increment(ref _sequence)} {message}");
        }
    }
}
