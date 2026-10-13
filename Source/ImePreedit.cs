using System;

namespace RimWorldKRInput
{
    internal static class ImePreedit
    {
        private static long _session;
        private static int _owner;
        private static string _text = "";
        private static bool _cancelled;
        internal static int ActiveControl => !_cancelled && _text.Length != 0 ? _owner : 0;

        internal static void Update(string text, long session, int control)
        {
            bool changed = session != _session || text != _text;
            if (session != _session)
            {
                _session = session;
                _owner = control;
                _cancelled = false;
            }
            _text = text;
            if (changed)
                ImeProbe.Write($"Preedit: control={_owner} text={ImeProbe.Quote(text)}");
        }

        internal static void Cancel()
        {
            if (!_cancelled && _text.Length != 0)
                ImeProbe.Write("Preedit: display cancelled after focus/caret interaction.");
            _cancelled = true;
        }

        internal static string ForControl(int control)
        {
            if (control != _owner)
                Cancel();
            return !_cancelled && control != 0 ? _text : "";
        }

        internal static string DisplayText(string text, string preedit, int caret, int anchor,
            out int displayCaret, out int displayAnchor)
        {
            int start = Math.Clamp(Math.Min(caret, anchor), 0, text.Length);
            int end = Math.Clamp(Math.Max(caret, anchor), 0, text.Length);
            displayAnchor = start;
            displayCaret = start + preedit.Length;
            return text.Remove(start, end - start).Insert(start, preedit);
        }
    }
}
