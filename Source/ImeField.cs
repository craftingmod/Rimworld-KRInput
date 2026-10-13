using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RimWorldKRInput
{
    internal static class ImeField
    {
        private sealed class Pending
        {
            internal string Base;
            internal string Returned;
            internal int Caret;
            internal int Anchor;
        }

        private static readonly Dictionary<int, Pending> Fields = new Dictionary<int, Pending>();
        private static readonly HashSet<int> ComposingThisFrame = new HashSet<int>();

        internal static void BeginFrame()
        {
            // RunKeyPass has a layout pass before keyDown; keep IME key ownership for the whole frame.
            ComposingThisFrame.Clear();
            if (ImePreedit.ActiveControl != 0)
                ComposingThisFrame.Add(ImePreedit.ActiveControl);
        }

        internal static bool WasComposing(int control) => ComposingThisFrame.Contains(control);

        internal static string Prepare(int control, string text, ref int caret, ref int anchor,
            out bool hadPreedit)
        {
            hadPreedit = Fields.TryGetValue(control, out var pending);
            if (!hadPreedit)
                return text;
            if (text != pending.Returned && text != pending.Base)
            {
                Fields.Remove(control);
                hadPreedit = false;
                if (control == GUIUtility.keyboardControl)
                    ImePreedit.Cancel();
                return text;
            }
            if (control == GUIUtility.keyboardControl)
            {
                ComposingThisFrame.Add(control);
                caret = pending.Caret;
                anchor = pending.Anchor;
                var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), control);
                editor.cursorIndex = caret;
                editor.selectIndex = anchor;
            }
            return pending.Base;
        }

        internal static bool Preview(string text, string preedit, int caret, int anchor,
            int maxLength, Regex validator, out string display, out int displayCaret, out int displayAnchor)
        {
            display = ImePreedit.DisplayText(text, preedit, caret, anchor, out displayCaret, out displayAnchor);
            return display.Length <= maxLength && (validator == null || validator.IsMatch(display));
        }

        internal static string Finish(int control, string text, bool focused, bool multiline,
            ref int caret, ref int anchor, int maxLength, Regex validator)
        {
            string preedit = focused && !multiline ? ImePreedit.ForControl(control) : "";
            if (preedit.Length == 0 || !Preview(text, preedit, caret, anchor, maxLength, validator,
                out string display, out int displayCaret, out _))
            {
                Fields.Remove(control);
                return text;
            }
            Fields[control] = new Pending { Base = text, Returned = display, Caret = caret, Anchor = anchor };
            caret = displayCaret;
            anchor = displayCaret;
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), control);
            editor.text = display;
            editor.cursorIndex = caret;
            editor.selectIndex = anchor;
            return display;
        }
    }
}
