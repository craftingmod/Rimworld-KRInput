using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorldKRInput;
using UnityEngine;

static class Program
{
    static void Check(bool condition, string message)
    {
        if (!condition)
        {
            Console.WriteLine(string.Join(Environment.NewLine, Verse.Log.Messages));
            throw new Exception(message);
        }
    }

    static int FieldLogs() => Verse.Log.Messages.Count(line => line.Contains("Field:"));

    static void Main()
    {
        ImeProbe.Install();
        Check(Verse.Log.Messages.Count(line => line.Contains("Patched:")) == 9, "All nine hooks must install");
        Check(!Verse.Log.Messages.Any(line => line.Contains("Patch failed for")), "Patch installation failed");
        var keyboard = new UnityEngine.InputSystem.Keyboard();
        keyboard.OnIMECompositionChanged("안".ToCharArray());
        keyboard.OnIMECompositionChanged(Array.Empty<char>());
        Check(keyboard.Calls == 2, "Composition original must run");
        Check(Verse.Log.Messages.Any(line => line.Contains("Composition callback: text=\"안\"")), "Preedit payload missing");
        Check(Verse.Log.Messages.Any(line => line.Contains("Composition callback: text=\"\"")), "Empty composition missing");
        var dispatch = new RimWorldWaylith.Imgui.ImguiDispatch();
        dispatch.Send('안');
        Check(dispatch.Text == "안", "Commit must be inserted exactly once by the original");
        Check(Verse.Log.Messages.Any(line => line.Contains("U+C548")), "Commit code point missing");
        var backend = new Waylith.Input.GlfwInputSource();
        backend.Send(0x1F600);
        Check(backend.CodePoint == 0x1F600, "Backend original must receive the unchanged code point");
        Check(Verse.Log.Messages.Any(line => line.Contains("GLFW commit: U+1F600")), "Backend code point missing");

        GUIUtility.keyboardControl = 1;
        Event.current = new Event { type = EventType.keyDown, keyCode = KeyCode.None };
        Check(GUI.Field("안") == "안", "Probe changed the field result");
        int initialLogs = FieldLogs();
        Check(initialLogs == 1, "Focused field must log");
        GUI.Field("안");
        GUI.Field("unfocused", focused: false);
        GUI.Field("안");
        Check(FieldLogs() == initialLogs, "Repeated repaint/unfocused fields must not spam");
        GUI.Field("안녕");
        Check(FieldLogs() == initialLogs + 1, "Text change must log");
        GUI.MoveCaret(1, 0);
        GUI.Field("안녕");
        Check(FieldLogs() == initialLogs + 2, "Caret/selection change must log");
        GUIUtility.keyboardControl = 2;
        GUI.Field("안녕");
        Check(FieldLogs() == initialLogs + 3, "Focus change must log");
        try { GUI.Field("throw"); throw new Exception("Original exception swallowed"); }
        catch (InvalidOperationException) { }
        GUI.Field("after exception");
        Check(FieldLogs() == initialLogs + 4, "Trace context must recover after an exception");
        GUI.Field("nested");
        Check(Verse.Log.Messages.Any(line => line.Contains("text=\"nested\" -> \"nested\"")), "Nested call lost outer trace");
        Check(GUI.DrawCalls > 0, "Original renderer must run");
        Console.WriteLine("PASS: real Harmony hooks on fixtures; original calls, composition/commit, focus, deduplication, caret, nesting and exception recovery.");
        FieldReturnSmoke.Run();
        NativeSmoke.Run();
        DebugShortcutSmoke.Run();
    }
}

namespace Verse
{
    static class Log
    {
        internal static readonly List<string> Messages = new();
        public static void Message(string message) => Messages.Add(message);
        public static void Warning(string message) => Messages.Add(message);
    }
}

namespace UnityEngine.InputSystem
{
    class Keyboard
    {
        internal int Calls;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnIMECompositionChanged(char[] composition) => Calls++;
    }
}

namespace RimWorldWaylith.Imgui
{
    class ImguiDispatch
    {
        internal string Text = "";
        internal void Send(char character) => OnTextInput(character);
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void DispatchInput(object host) { }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void OnTextInput(char character) => Text += character;
        private readonly List<char> _typedChars = new();
        internal void SendKeys(string text, UnityEngine.EventModifiers mods = UnityEngine.EventModifiers.None)
        {
            _typedChars.AddRange(text);
            DispatchKeyboard(default, mods);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DispatchKeyboard(UnityEngine.Vector2 mouse, UnityEngine.EventModifiers mods)
        {
            foreach (char character in _typedChars)
                RunKeyPass(UnityEngine.KeyCode.None, character, mouse, mods);
            _typedChars.Clear();
            RunKeyPass(UnityEngine.KeyCode.RightBracket, '\0', mouse, mods);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RunKeyPass(UnityEngine.KeyCode key, char character, UnityEngine.Vector2 mouse,
            UnityEngine.EventModifiers mods)
        {
            if (character != '\0') Text += character;
            else Text += "<bracket-key>";
        }
    }
}

namespace Waylith.Input
{
    class GlfwInputSource
    {
        internal uint CodePoint;
        internal void Send(uint codePoint) => OnChar(null, codePoint);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void OnChar(object window, uint codePoint) => CodePoint = codePoint;
    }
}

namespace UnityEngine
{
    struct Rect { }
    class GUIStyle { internal bool Focused = true; internal int Control; }
    class GUIContent { public string text; }
    class TextEditor { public string text; public int cursorIndex; public int selectIndex; }
    enum EventType { keyDown, repaint, mouseDown, layout }
    enum KeyCode { None, Escape, Backspace, Delete, LeftArrow, RightArrow, UpArrow, DownArrow, Home, End, Return, KeypadEnter, RightBracket }
    class Event
    {
        public static Event current;
        public EventType type;
        public EventType rawType;
        public KeyCode keyCode;
        public char character;
        public void Use() { }
    }
    static class GUIUtility
    {
        public static int keyboardControl;
        private static readonly Dictionary<int, TextEditor> Editors = new();
        public static object GetStateObject(Type type, int control)
        {
            if (!Editors.TryGetValue(control, out var editor)) Editors[control] = editor = new TextEditor();
            return editor;
        }
    }
    static class GUI
    {
        private static int _caretIndex;
        private static int _selectAnchor;
        private static float _textScrollX;
        internal static int DrawCalls;
        public static bool changed;
        internal static string RenderedText;
        internal static int RenderedCaret;
        internal static int RenderedAnchor;
        internal static bool ThrowOnDraw;
        internal static (int, int, float) State => (_caretIndex, _selectAnchor, _textScrollX);
        internal static void SetState(int caret, int anchor, float scroll)
        { _caretIndex = caret; _selectAnchor = anchor; _textScrollX = scroll; }
        internal static void MoveCaret(int caret, int anchor) { _caretIndex = caret; _selectAnchor = anchor; }
        internal static string Field(string text, bool focused = true, bool multiline = false, int maxLength = -1, int? control = null)
        {
            var content = new GUIContent { text = text };
            var style = new GUIStyle { Focused = focused, Control = control ?? (focused ? GUIUtility.keyboardControl : GUIUtility.keyboardControl + 1) };
            DoTextField(new Rect(), style.Control, content, multiline, maxLength, style, null, '\0');
            return content.text;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DoTextField(Rect rect, int id, GUIContent content, bool multiline, int maxLength, GUIStyle style, string secureText, char maskChar)
        {
            content.text = TextFieldImpl(rect, secureText ?? content.text, style, multiline);
            if (maxLength >= 0 && content.text.Length > maxLength) content.text = content.text.Substring(0, maxLength);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string TextFieldImpl(Rect rect, string text, GUIStyle style, bool multiline)
        {
            if (text == "throw") throw new InvalidOperationException();
            int control = style.Control;
            TrackControlWindow(control);
            if (text == "nested") Field("inner");
            if (style.Focused && Event.current?.type == EventType.keyDown)
                text = ApplyTextKey(text, Event.current, multiline);
            DrawTextFieldChrome(rect, text, 1, style.Focused, multiline, style, null);
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), control);
            editor.text = text;
            if (style.Focused) { editor.cursorIndex = _caretIndex; editor.selectIndex = _selectAnchor; }
            return text;
        }
        private static void TrackControlWindow(int control) { }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ApplyTextKey(string text, Event e, bool multiline)
        {
            _caretIndex = Math.Clamp(_caretIndex, 0, text.Length);
            _selectAnchor = Math.Clamp(_selectAnchor, 0, text.Length);
            if (e.character != '\0' && !char.IsControl(e.character))
            {
                int start = Math.Min(_caretIndex, _selectAnchor), end = Math.Max(_caretIndex, _selectAnchor);
                text = text.Remove(start, end - start).Insert(start, e.character.ToString());
                _caretIndex = start + 1;
                _selectAnchor = _caretIndex;
            }
            else if (e.keyCode == KeyCode.Backspace && _caretIndex > 0)
            {
                text = text.Remove(--_caretIndex, 1);
                _selectAnchor = _caretIndex;
            }
            return text;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DrawTextFieldChrome(Rect rect, string text, int font, bool focused, bool multiline, GUIStyle style, object backend)
        {
            DrawCalls++;
            RenderedText = text;
            RenderedCaret = _caretIndex;
            RenderedAnchor = _selectAnchor;
            _textScrollX += 10;
            if (ThrowOnDraw) throw new InvalidOperationException("Renderer failed");
        }
    }
}
