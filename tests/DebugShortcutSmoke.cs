using System;
using UnityEngine;
using UnityStubs.Internal;

static class DebugShortcutSmoke
{
    internal static void Run()
    {
        RimWorldKRInput.ImeResetShortcut.Install();
        var dispatch = new RimWorldWaylith.Imgui.ImguiDispatch();
        Verse.Prefs.DevMode = true;
        GUIUtility.keyboardControl = 42;
        RawKeys.Pressed = RawKeys.Held = true;
        dispatch.SendKeys("안]");
        Check(dispatch.Text == "안" && RimWorldKRInput.ImeResetButton.Calls == 1
            && GUIUtility.keyboardControl == 42, "Reset must retain focus and suppress only the bracket");
        RawKeys.Pressed = false;
        dispatch.SendKeys("]");
        Check(dispatch.Text == "안" && RimWorldKRInput.ImeResetButton.Calls == 1,
            "Holding/repeating the key must not reset again or insert brackets");
        RawKeys.Held = false;
        dispatch.SendKeys("]");
        Check(dispatch.Text == "안]", "Text without a physical bracket press must be preserved");
        Verse.Prefs.DevMode = false;
        RawKeys.Pressed = RawKeys.Held = true;
        dispatch.SendKeys("]");
        Check(dispatch.Text.EndsWith("]<bracket-key>") && RimWorldKRInput.ImeResetButton.Calls == 1,
            "Normal mode must preserve characters and game keys");
        Verse.Prefs.DevMode = true;
        dispatch.SendKeys("}", EventModifiers.Shift);
        Check(dispatch.Text.EndsWith("}<bracket-key>") && RimWorldKRInput.ImeResetButton.Calls == 1,
            "Modified keys must not invoke reset");
        Console.WriteLine("PASS: debug bracket shortcut; focus, character/game-key suppression, repeat, normal mode and modifiers. Reset is a fixture.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

namespace Verse { static class Prefs { public static bool DevMode; } }
namespace UnityEngine
{
    struct Vector2 { }
    enum EventModifiers { None, Shift, Control }
}
namespace UnityStubs.Internal
{
    static class RawKeys
    {
        internal static bool Pressed, Held;
        public static bool GetKeyDown(KeyCode key) => Pressed;
        public static bool GetKey(KeyCode key) => Held;
    }
}
namespace RimWorldKRInput
{
    static class ImeResetButton
    {
        internal static int Calls;
        public static void ResetIme() => Calls++;
    }
}
