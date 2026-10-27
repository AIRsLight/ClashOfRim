using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim;
using HarmonyLib;
using UnityEngine;
using UnityStubs.Gui;

internal static class ClientPasswordFieldTests
{
    private static Vector2 candidatePosition;
    private static int candidatePositionUpdates;
    internal static void Run()
    {
        var previous = GuiBackend.Current; var previousEvent = Event.current;
        int previousFocus = GUIUtility.keyboardControl;
        var backend = new Backend(); var harmony = new Harmony("ClashOfRim.Tests.PasswordField");
        var ime = AccessTools.TypeByName("UnityStubs.Internal.ImeComposition");
        try
        {
            GuiBackend.Current = backend;
            harmony.Patch(AccessTools.PropertyGetter(typeof(GUIUtility), "systemCopyBuffer"), prefix: new HarmonyMethod(typeof(ClientPasswordFieldTests), nameof(Clipboard)));
            harmony.Patch(AccessTools.Method(ime, "SetCandidatePos"), prefix: new HarmonyMethod(typeof(ClientPasswordFieldTests), nameof(CandidatePosition)));
            string value = "Secret-密<color=red>码</color>";
            GUI.FocusControl("password-test");
            Check(Draw(value, new Event { type = EventType.Repaint }) == value, "Password drawing changed the credential.");
            Check(backend.Drawn.Last() == new string('*', value.Length), "Repaint exposed the password.");
            value = Draw(value, Key(KeyCode.End));
            value = Draw(value, Key(KeyCode.None, character: 'Z'));
            Check(value.EndsWith("Z", StringComparison.Ordinal), "Native typing was lost.");
            value = Draw(value, Key(KeyCode.Backspace));
            Check(value == "Secret-密<color=red>码</color>", "Native backspace corrupted the password.");
            value = Draw(value, Key(KeyCode.A, control: true));
            value = Draw(value, Key(KeyCode.V, control: true));
            Check(value == "pasted-密🔑", "Native selection/paste changed the credential.");
            ime.GetMethod("Set")!.Invoke(null, new object[] { "输入中", 3 });
            candidatePositionUpdates = 0;
            backend.DevicePointUpdates = 0;
            Check(Draw(value, new Event { type = EventType.Repaint }) == value, "Uncommitted IME text changed the password.");
            Check(backend.Drawn.Last().Length > value.Length, "IME composition was not exercised.");
            Check(backend.Text.All(t => t.All(c => c == '*')) && !backend.RichText, "Password/IME leaked to drawing or cursor measurement.");
            Check(candidatePositionUpdates > 0 && backend.DevicePointUpdates > 0,
                "IME candidate positioning did not use the native device-coordinate conversion.");
            Check(candidatePosition == backend.LastDevicePoint && candidatePosition != backend.LastGuiPoint,
                "The masking backend lost the device scale/offset used for IME candidates.");
            ime.GetMethod("Set")!.Invoke(null, new object[] { "", 0 });
            backend.Throw = true;
            try { Draw(value, new Event { type = EventType.Repaint }); throw new InvalidOperationException("Expected draw fault."); }
            catch (InvalidOperationException ex) when (ex.Message == "draw fault") { }
            Check(ReferenceEquals(GuiBackend.Current, backend), "Drawing failure leaked the masking backend.");
            backend.Throw = false;
            Event.current = new Event { type = EventType.Repaint };
            GUI.TextField(new Rect(0, 50, 260, 28), "ordinary field");
            Check(backend.Drawn.Last() == "ordinary field", "Password rendering masked a later ordinary field.");
        }
        finally
        {
            ime.GetMethod("Set")!.Invoke(null, new object[] { "", 0 });
            harmony.UnpatchAll(harmony.Id); GUIUtility.keyboardControl = previousFocus;
            GuiBackend.Current = previous; Event.current = previousEvent;
        }
    }
    private static string Draw(string value, Event input)
    {
        AccessTools.Method(typeof(GUI), "ResetNamedControlOccurrences").Invoke(null, null);
        GUI.SetNextControlName("password-test"); Event.current = input;
        return ClashPasswordField.Draw(new Rect(0, 0, 260, 28), value);
    }
    private static Event Key(KeyCode key, bool control = false, char character = '\0') => new()
    { type = EventType.KeyDown, keyCode = key, character = character, modifiers = control ? EventModifiers.Control : EventModifiers.None };
    private static bool Clipboard(ref string __result) { __result = "pasted-密🔑"; return false; }
    private static bool CandidatePosition(Vector2 __0) { candidatePosition = __0; candidatePositionUpdates++; return false; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Backend : IGuiBackend
    {
        internal readonly List<string> Text = new(), Drawn = new();
        internal bool Throw, RichText;
        internal int DevicePointUpdates;
        internal Vector2 LastGuiPoint, LastDevicePoint;
        private void Read(string text, bool rich) { Text.Add(text); RichText |= rich; }
        public Vector2 MeasureText(int font, float size, FontStyle style, string text, float width, bool rich) { Read(text, rich); return new(text.Length * 6, 20); }
        public int TextIndexAt(int font, float size, FontStyle style, string text, float width, Vector2 point, bool rich) { Read(text, rich); return Math.Clamp((int)(point.x / 6), 0, text.Length); }
        public Vector2 TextCursorPos(int font, float size, FontStyle style, string text, float width, int index, out float height, bool rich) { Read(text, rich); height = 20; return new(index * 6, 0); }
        public void TextSelectionRects(int font, float size, FontStyle style, string text, float width, int start, int end, List<Rect> rects, bool rich) => Read(text, rich);
        public void DrawText(Rect rect, string text, int font, float size, FontStyle style, TextAnchor anchor, bool wrap, Color color, Rect clip, bool rich)
        { Read(text, rich); Drawn.Add(text); if (Throw) throw new InvalidOperationException("draw fault"); }
        public bool MouseOutsideHitClip => false;
        public int GroupCount => 0;
        public bool InlineControlsObscured => false;
        public float LineSpacing(int font, float size) => 20;
        public void FontMetrics(int font, out float ascent, out float descent, out float gap) { ascent = 15; descent = 5; gap = 0; }
        public int RegisterFont(string name, byte[] data, float size, int index) => 0;
        public void DrawTexture(Rect rect, Texture texture, ScaleMode mode, Color color) { }
        public void DrawTextureWithTexCoords(Rect rect, Texture texture, Rect uv, Color color) { }
        public void DrawTextureWithMaterial(Rect rect, Texture texture, Rect uv, Color color, Material material, Waylith.Rendering.Material native) { }
        public void FillRect(Rect rect, Color color) { }
        public void FillRectRounded(Rect rect, Color color, float radius) { }
        public void DrawTextureQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Texture texture, Color color) { }
        public void DrawPrimitives(int mode, ReadOnlySpan<GuiVertex> vertices, Texture texture, bool wireframe) { }
        public void SetMatrix(Matrix4x4 matrix) { }
        public void PushGroup(Rect rect) { }
        public void PushScrollView(Rect rect, Vector2 scroll) { }
        public void PopGroup() { }
        public void GetGroup(int index, out Rect rect, out Vector2 offset) { rect = default; offset = default; }
        public void BeginWindowContent(Rect rect) { }
        public void EndWindowContent() { }
        public void PushClipRect(Rect rect) { }
        public void PopClipRect() { }
        public void ActiveRenderTextureChanged(RenderTexture texture) { }
        public void SetTextLanguage(string language) { }
        public Vector2 ScreenToGuiPoint(Vector2 point) => point;
        public Vector2 GuiToScreenPoint(Vector2 point) => point;
        public Vector2 GuiToDevicePoint(Vector2 point)
        {
            DevicePointUpdates++;
            LastGuiPoint = point;
            LastDevicePoint = new Vector2(point.x * 1.5f + 37, point.y * 1.5f + 61);
            return LastDevicePoint;
        }
    }
}
