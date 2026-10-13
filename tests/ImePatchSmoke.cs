using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using HarmonyLib;
using RimWorldKRInput;
using UnityEngine;
using UnityStubs.Gui;

internal static class Program
{
    static void Main()
    {
        string root = Environment.GetEnvironmentVariable("RIMWORLD_DIR") ?? @"G:\SteamLibrary\steamapps\common\RimWorld";
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(root, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, name.Name + ".dll")) : null;
        Run();
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        Assembly.Load("RimWorld");
        ImePatches.Install();
        int count = 0;
        foreach (var method in Harmony.GetAllPatchedMethods())
            if (Harmony.GetPatchInfo(method).Owners.Contains("rimworld.krinput.ime")) count++;
        Check(count == 15, "All 15 real game hooks must bind: " + count);
        NativeLifecycle();
        ImeState.Owner = 42; ImeState.Caret = 3; ImeState.Anchor = 1;
        ImeState.Begin(); ImeState.Update("", "안", 1);
        Check(ImeState.Preview("abcd", out int caret) == "a안d" && caret == 2, "Selection preview/cursor");
        ImeState.Update("안", "ㄴ", 1);
        ImeState.Update("녕", "", 0);
        Check(ImeState.Results.Count == 2, "Results must not overwrite each other");
        var first = ImeState.Results.Dequeue(); var second = ImeState.Results.Dequeue();
        Check(first.Owner == 42 && first.Caret == 3 && first.Anchor == 1 && second.Caret == 2, "Commit owner/ordered insertion");
        ImeState.Update("ㅋㅋ", "", 0);
        Check(ImeState.Results.Dequeue().Text == "ㅋㅋ", "Repeated characters must survive");
        ImeState.ClearComposition();
        ImeState.Caret = ImeState.Anchor = 1;
        ImeState.Begin(); ImeState.Update("안", "", 0); WindowsImeBridge.QueueResults();
        UnityEngine.InputSystem.InputSystem.QueueTextInput('a');
        ImeState.Update("녕", "", 0); WindowsImeBridge.QueueResults();
        var dispatchType = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImguiDispatch");
        var dispatchFixture = RuntimeHelpers.GetUninitializedObject(dispatchType);
        var onText = AccessTools.Method(dispatchType, "OnTextInput");
        foreach (char ch in "안a녕") onText.Invoke(dispatchFixture, new object[] { ch });
        Check(ImeState.Commits.Count == 3 && ImeState.Commits.Dequeue().Text == "안"
            && ImeState.Commits.Dequeue().Text == "a", "Native and GLFW input share one ordered character queue");
        Check(ImeState.Commits.Dequeue().Caret == 3, "Interleaved Latin character advances next IME insertion");
        ImeState.ClearComposition();
        Check(Input.compositionString == "", "Unity empty composition API");
        Input.imeCompositionMode = IMECompositionMode.On;

        var backend = DispatchProxy.Create<IGuiBackend, FakeBackend>();
        var fake = (FakeBackend)(object)backend;
        var guiBackend = AccessTools.TypeByName("UnityStubs.Gui.GuiBackend");
        AccessTools.Property(guiBackend, "Current").SetValue(null, backend);
        var draw = AccessTools.Method(typeof(GUI), "DrawTextFieldChrome");
        var field = AccessTools.Method(typeof(GUI), "TextFieldImpl");
        var track = AccessTools.Method(typeof(GUI), "TrackControlWindow");
        var caretField = AccessTools.Field(typeof(GUI), "_caretIndex");
        var anchorField = AccessTools.Field(typeof(GUI), "_selectAnchor");
        var xField = AccessTools.Field(typeof(GUI), "_textScrollX");
        var yField = AccessTools.Field(typeof(GUI), "_textScrollY");
        var rect = new Rect(10, 10, 120, 30);
        var style = new GUIStyle();
        style.normal.background = new Texture2D(1, 1);
        var controlFor = AccessTools.Method(typeof(GUI), "ControlIdFor");
        int id = (int)controlFor.Invoke(null, new object[] { rect });
        GUI.enabled = true; GUIUtility.keyboardControl = id;
        Event.current = new Event { type = EventType.repaint };
        caretField.SetValue(null, 1); anchorField.SetValue(null, 1);
        Check((string)field.Invoke(null, new object[] { rect, "ab", style, false }) == "ab", "Initial real GUI return");
        ImeState.Begin(); ImeState.Update("", "안", 1);
        int beforeCaret = (int)caretField.GetValue(null);
        float beforeX = (float)xField.GetValue(null), beforeY = (float)yField.GetValue(null);
        Check((string)field.Invoke(null, new object[] { rect, "ab", style, false }) == "ab", "Preedit must never escape through return");
        Check(fake.Text == "a안b", "Real renderer receives preedit once: " + fake.Text);
        var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), id);
        Check(editor.text == "ab" && editor.cursorIndex == beforeCaret, "Editor remains committed");
        Check((int)caretField.GetValue(null) == beforeCaret && (float)xField.GetValue(null) == beforeX && (float)yField.GetValue(null) == beforeY, "Temporary render state restores");
        ImeState.Update("ㅋㅋ", "ㄴ", 1);
        WindowsImeBridge.QueueResults();
        foreach (char ch in "ㅋㅋ") onText.Invoke(dispatchFixture, new object[] { ch });
        var runKeyField = AccessTools.Field(typeof(ImePatches), "RunKeyPass");
        var originalKeyPass = runKeyField.GetValue(null);
        var receiver = new CommitReceiver { Field = field, Rect = rect, Style = style, Text = "ab" };
        GUIUtility.keyboardControl = 99;
        try
        {
            runKeyField.SetValue(null, typeof(CommitReceiver).GetMethod("Pass"));
            AccessTools.Method(typeof(ImePatches), "DispatchCommits").Invoke(null, new object[] { receiver, Vector2.zero, EventModifiers.None });
        }
        finally { runKeyField.SetValue(null, originalKeyPass); }
        Check(receiver.Text == "aㅋㅋb" && receiver.Passes == 2, "Real GUI commits repeated characters once into old owner");
        Check(GUIUtility.keyboardControl == 99, "Commit delivery restores new focus");
        GUIUtility.keyboardControl = id;
        Event.current = new Event { type = EventType.repaint };
        Check((string)field.Invoke(null, new object[] { rect, receiver.Text, style, false }) == receiver.Text && fake.Text == "aㅋㅋㄴb", "Result + next composition ordering");
        field.Invoke(null, new object[] { rect, "external", style, false });
        Check(ImeState.Composition == "" && fake.Text == "external", "External value change cancels old composition");
        ImeState.Begin(); ImeState.Update("", "안", 1);
        GUI.PasswordField(rect, "external", '*', style);
        Check(ImeState.Owner == 0 && ImeState.Composition == "", "Password fields must not render preedit");
        style.wordWrap = true;
        caretField.SetValue(null, 2); anchorField.SetValue(null, 2);
        editor.cursorIndex = editor.selectIndex = 2;
        field.Invoke(null, new object[] { rect, "a\nb", style, true });
        ImeState.Begin(); ImeState.Update("", "안", 1);
        Check((string)field.Invoke(null, new object[] { rect, "a\nb", style, true }) == "a\nb" && fake.Text == "a\n안b", "TextArea uses the original multiline renderer without changing return");
        WindowsImeBridge.Cancel();
        var readOnlyStyle = new GUIStyle();
        field.Invoke(null, new object[] { rect, "readonly", readOnlyStyle, true });
        Check(ImeState.Owner == 0, "Read-only TextArea does not acquire IME focus");
        field.Invoke(null, new object[] { rect, "ab", style, false });
        ImeState.Begin(); ImeState.Update("", "안", 1);
        beforeCaret = (int)caretField.GetValue(null);
        beforeX = (float)xField.GetValue(null);
        ImeState.ConsumedKeys.Add(KeyCode.Backspace);
        var keyPrefix = AccessTools.Method(typeof(ImePatches), "KeyPrefix");
        Check(!(bool)keyPrefix.Invoke(null, new object[] { KeyCode.Backspace, '\0', EventType.keyDown })
            && (bool)keyPrefix.Invoke(null, new object[] { KeyCode.Backspace, '\0', EventType.keyUp })
            && (bool)keyPrefix.Invoke(null, new object[] { KeyCode.Escape, '\0', EventType.keyDown }), "Only observed IME keyDown is consumed");
        fake.Throw = true;
        try { field.Invoke(null, new object[] { rect, "ab", style, false }); throw new Exception("Expected renderer exception"); }
        catch (TargetInvocationException) { }
        Check((int)caretField.GetValue(null) == beforeCaret && (float)xField.GetValue(null) == beforeX, "Finalizer restores on exceptions");
        fake.Throw = false;
        new Harmony("rimworld.krinput.ime").UnpatchAll("rimworld.krinput.ime");
        Console.WriteLine("PASS: 15 real-game Harmony bindings, ordered results, repeated text, selection/cursor, render-only preview, editor preservation, exception recovery.");
    }

    static void NativeLifecycle()
    {
        nint window = CreateWindowExW(0, "STATIC", "KRInput smoke", 0, 0, 0, 100, 100, 0, 0, 0, 0);
        Check(window != 0, "Hidden native window creation");
        nint context = ImmCreateContext();
        if (context == 0)
        {
            DestroyWindow(window);
            Console.WriteLine("SKIP: this process cannot create an IMM context; native lifecycle is unverified.");
            return;
        }
        nint previous = ImmAssociateContext(window, context);
        try
        {
            Check(WindowsImeBridge.Attach(window), "Same-thread subclass installation");
            // No foreground activation or real input is needed for this ownership check.
            AccessTools.Method(typeof(WindowsImeBridge), "SetEnabled").Invoke(null, new object[] { false });
            Check(ImmGetContext(window) == 0, "Context detaches outside text focus");
            WindowsImeBridge.Stop();
            nint restored = ImmGetContext(window);
            Check(restored == context, "Borrowed context restores on shutdown");
            if (restored != 0) ImmReleaseContext(window, restored);
        }
        finally
        {
            WindowsImeBridge.Stop();
            ImmAssociateContext(window, previous);
            ImmDestroyContext(context);
            DestroyWindow(window);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("imm32.dll")] static extern int ImmGetCompositionStringW(nint context, uint index, nint buffer, uint bytes);
    [DllImport("imm32.dll")] static extern nint ImmCreateContext();
    [DllImport("imm32.dll")] static extern bool ImmDestroyContext(nint context);
    [DllImport("imm32.dll")] static extern nint ImmAssociateContext(nint hwnd, nint context);
    [DllImport("imm32.dll")] static extern nint ImmGetContext(nint hwnd);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(nint hwnd, nint context);
}

public class CommitReceiver
{
    public MethodInfo Field;
    public Rect Rect;
    public GUIStyle Style;
    public string Text;
    public int Passes;
    public void Pass(KeyCode key, char character, Vector2 mouse, EventModifiers mods, EventType type)
    {
        Passes++;
        Event.current = new Event { type = type, keyCode = key, character = character, modifiers = mods };
        Text = (string)Field.Invoke(null, new object[] { Rect, Text, Style, false });
    }
}

public class FakeBackend : DispatchProxy
{
    public string Text;
    public bool Throw;
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "DrawText") { Text = (string)args[1]; if (Throw) throw new InvalidOperationException("Renderer fixture"); }
        if (method.Name == "TextCursorPos") { args[6] = 16f; return new Vector2((int)args[5] * 8, 0); }
        if (method.Name == "GuiToScreenPoint") return args[0];
        if (method.Name == "TextSelectionRects") ((System.Collections.Generic.List<Rect>)args[7]).Add(new Rect(8, 0, 8, 16));
        return method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
