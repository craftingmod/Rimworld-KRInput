using System;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RimWorldKRInput;
using UnityEngine;

static class FieldReturnSmoke
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    static void Sync()
    {
        WindowsImeProbe.GetComposition(out string text, out long session);
        ImePreedit.Update(text, session, GUIUtility.keyboardControl);
    }
    static void Compose(string text, bool fresh = false)
    {
        if (fresh) WindowsImeProbe.SetComposition(0x010E, 0, null);
        WindowsImeProbe.SetComposition(0x010F, 8, text);
        Sync();
    }
    static void Pass(EventType type, char character = '\0', KeyCode key = KeyCode.None, bool newFrame = true)
    {
        if (newFrame) ImeField.BeginFrame();
        Event.current = new Event { type = type, rawType = type, character = character, keyCode = key };
    }
    static void End()
    {
        WindowsImeProbe.SetComposition(0x010E, 0, null);
        Sync();
    }
    internal static void Run()
    {
        GUIUtility.keyboardControl = 10;
        GUI.SetState(0, 0, 0);
        Pass(EventType.repaint);
        Compose("ㅇ", true);
        string text = GUI.Field("");
        Check(text == "ㅇ" && GUI.changed, "Preedit not returned as a native field change");
        Pass(EventType.layout);
        text = GUI.Field(text);
        Check(text == "ㅇ", "Layout accumulated composition");
        Compose("아");
        text = GUI.Field(text);
        Check(text == "아", "Composition appended instead of replacing");
        Compose("안");
        Pass(EventType.repaint);
        text = GUI.Field(text);
        Check(text == "안" && GUI.RenderedText == "안", "Return and paint disagree");
        Check(((TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), 10)).text == "안", "Editor not synchronized");
        WindowsImeProbe.SetComposition(0x010F, 0x800, null);
        Compose("ㄴ");
        Pass(EventType.keyDown, '안');
        text = GUI.Field(text);
        Check(text == "안ㄴ", "Commit plus next composition duplicated/lost text");
        Compose("녀");
        Pass(EventType.repaint);
        text = GUI.Field(text);
        Check(text == "안녀", "Next composition not replaced");
        Compose("녕");
        text = GUI.Field(text);
        Check(text == "안녕", "Final composition not returned");
        WindowsImeProbe.SetComposition(0x010F, 0x800, null);
        Sync();
        Pass(EventType.keyDown, '녕');
        text = GUI.Field(text);
        Check(text == "안녕" && GUI.State.Item1 == 2, "Final commit inserted twice");
        Pass(EventType.keyDown, '.');
        text = GUI.Field(text);
        Check(text == "안녕.", "Normal character path changed");

        GUIUtility.keyboardControl = 20;
        GUI.SetState(3, 1, 4);
        Compose("안", true);
        Pass(EventType.repaint);
        text = GUI.Field("ABCD");
        Check(text == "A안D", "Selection replacement wrong");
        End();
        text = GUI.Field(text);
        Check(text == "ABCD" && GUI.State.Item1 == 3 && GUI.State.Item2 == 1, "Cancellation lost replaced selection");
        GUI.SetState(1, 1, 4);
        Compose("안", true);
        text = GUI.Field("XY");
        Check(text == "X안Y", "Middle insertion wrong");
        GUIUtility.keyboardControl = 21;
        Check(GUI.Field("other") == "other", "Composition moved to another field");
        Check(GUI.Field(text, focused: false, control: 20) == "XY", "Unfocused original field retained pending text");

        GUIUtility.keyboardControl = 30;
        GUI.SetState(1, 1, 0);
        Compose("안", true);
        text = GUI.Field("A");
        Compose("아");
        Pass(EventType.keyDown, key: KeyCode.Backspace);
        text = GUI.Field(text);
        Check(text == "A아", "IME Backspace also deleted committed text");
        End();
        Pass(EventType.layout);
        text = GUI.Field(text);
        Check(text == "A", "Pre-key layout failed to remove final preedit");
        Pass(EventType.keyDown, key: KeyCode.Backspace, newFrame: false);
        text = GUI.Field(text);
        Check(text == "A", "Last composition Backspace also deleted committed text");
        Pass(EventType.keyDown, key: KeyCode.Backspace);
        Check(GUI.Field(text) == "", "Normal Backspace was suppressed");

        GUIUtility.keyboardControl = 40;
        GUI.SetState(2, 2, 0);
        Compose("안녕", true);
        Pass(EventType.repaint);
        Check(GUI.Field("AA", maxLength: 3) == "AA", "Overlong preedit bypassed maxLength");
        Compose("안");
        text = GUI.Field("AA", maxLength: 3);
        Check(text == "AA안", "Allowed preedit rejected by maxLength");
        End();
        Check(GUI.Field(text, maxLength: 3) == "AA", "Truncation introduced a permanent composition fragment");
        GUIUtility.keyboardControl = 41;
        GUI.SetState(2, 2, 0);
        Compose("안", true);
        Check(Verse.Widgets.TextField(new Rect(), "12", 10, new Regex("^[0-9]+$")) == "12", "Validator bypassed");
        Check(GUI.RenderedText == "12", "Rejected preedit still rendered");
        Check(GUI.Field("12", multiline: true) == "12", "Multiline field altered");

        GUIUtility.keyboardControl = 50;
        GUI.SetState(0, 0, 6);
        Compose("ㅇ", true);
        text = GUI.Field("");
        Compose("아");
        GUI.ThrowOnDraw = true;
        try { GUI.Field(text); throw new Exception("Render exception swallowed"); }
        catch (InvalidOperationException) { }
        finally { GUI.ThrowOnDraw = false; }
        Check(GUI.State == (0, 0, 6f), "Render exception leaked temporary display indices/scroll");
        Check(GUI.Field(text) == "아", "Pending rollback lost after exception");
        Check(GUI.Field("external") == "external", "External replacement was overwritten");
        End();
        Pass(EventType.keyDown, 'a');
        GUI.SetState(0, 0, 0);
        Check(GUI.Field("") == "a", "English input changed");
        Pass(EventType.keyDown);
        Console.WriteLine("PASS: common field return/paint/editor, repeated composition, commit+next composition, cancellation/selection/focus, IME and normal Backspace, maxLength/regex, multiline exclusion, exceptions and English.");
    }
}

namespace Verse
{
    static class Widgets
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string TextField(Rect rect, string text, int maxLength, Regex validator)
        {
            string result = GUI.Field(text);
            return result.Length <= maxLength && (validator == null || validator.IsMatch(result)) ? result : text;
        }
    }
}
