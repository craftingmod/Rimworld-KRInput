using System;
using System.Collections.Generic;
using UnityEngine;

namespace RimWorldKRInput
{
    internal static class ImeState
    {
        internal sealed class Commit
        {
            internal int Owner;
            internal string Text;
            internal int Caret, Anchor;
            internal bool RestoreSelection = true;
        }

        internal static readonly Queue<Commit> Commits = new();
        internal static readonly Queue<Commit> Results = new();
        internal static readonly Queue<Commit> TextSources = new();
        internal static Commit NativeCommit;
        internal static readonly HashSet<KeyCode> ConsumedKeys = new();
        internal static KeyCode PendingControl;
        internal static int Owner, Caret, Anchor, SeenFrame, Frame;
        internal static int CompositionOwner, Start, End, Cursor;
        internal static string Composition = "";
        internal static string FieldText = "", CompositionText = "";
        internal static bool Composing;
        internal static Commit Delivering;
        internal static bool FirstCharacter;

        internal static void Begin()
        {
            Composing = true;
            CompositionOwner = Owner;
            CompositionText = FieldText;
            Start = Math.Min(Caret, Anchor);
            End = Math.Max(Caret, Anchor);
            Composition = "";
            Cursor = 0;
        }

        internal static void Update(string result, string composition, int cursor)
        {
            if (!Composing) Begin();
            if (!string.IsNullOrEmpty(result))
            {
                Results.Enqueue(new Commit { Owner = CompositionOwner, Text = result, Caret = Caret, Anchor = Anchor });
                Caret = Anchor = Start + result.Length;
                Start = End = Caret;
            }
            Composition = composition;
            Cursor = Math.Clamp(cursor, 0, composition.Length);
        }

        internal static void ClearComposition()
        {
            Composition = "";
            Cursor = 0;
            CompositionOwner = 0;
            Composing = false;
        }

        internal static string Preview(string text, out int caret)
        {
            int start = Math.Clamp(Start, 0, text.Length);
            int end = Math.Clamp(End, start, text.Length);
            caret = start + Cursor;
            return text.Remove(start, end - start).Insert(start, Composition);
        }

        internal static KeyCode ControlKey(int vk) => vk switch
        {
            8 => KeyCode.Backspace, 9 => KeyCode.Tab, 13 => KeyCode.Return,
            27 => KeyCode.Escape, 33 => KeyCode.PageUp, 34 => KeyCode.PageDown,
            35 => KeyCode.End, 36 => KeyCode.Home, 37 => KeyCode.LeftArrow,
            38 => KeyCode.UpArrow, 39 => KeyCode.RightArrow, 40 => KeyCode.DownArrow,
            46 => KeyCode.Delete, _ => KeyCode.None
        };

        internal static void ConsumeControl(KeyCode key)
        {
            if (key == KeyCode.None) return;
            ConsumedKeys.Add(key);
            if (key == KeyCode.Return) ConsumedKeys.Add(KeyCode.KeypadEnter);
        }
    }
}
