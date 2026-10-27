using System;
using System.Collections.Generic;
using UnityEngine;
using UnityStubs.Gui;

namespace AIRsLight.ClashOfRim;

internal static class ClashPasswordField
{
    internal static string Draw(Rect rect, string password)
    {
        var previous = GuiBackend.Current;
        try
        {
            // Waylith's PasswordField currently ignores maskChar. Keep native editing,
            // but mask text at the rendering/measurement boundary, including IME text.
            if (previous is not null) GuiBackend.Current = new MaskedBackend(previous);
            return GUI.PasswordField(rect, password, '*');
        }
        finally { GuiBackend.Current = previous; }
    }

    private sealed class MaskedBackend : IGuiBackend
    {
        private readonly IGuiBackend inner;
        internal MaskedBackend(IGuiBackend inner) => this.inner = inner;
        private static string Mask(string text) => new('*', text?.Length ?? 0);
        public Vector2 MeasureText(int font, float size, FontStyle style, string text, float width, bool rich) => inner.MeasureText(font, size, style, Mask(text), width, false);
        public int TextIndexAt(int font, float size, FontStyle style, string text, float width, Vector2 point, bool rich) => inner.TextIndexAt(font, size, style, Mask(text), width, point, false);
        public Vector2 TextCursorPos(int font, float size, FontStyle style, string text, float width, int index, out float height, bool rich) => inner.TextCursorPos(font, size, style, Mask(text), width, index, out height, false);
        public void TextSelectionRects(int font, float size, FontStyle style, string text, float width, int start, int end, List<Rect> rects, bool rich) => inner.TextSelectionRects(font, size, style, Mask(text), width, start, end, rects, false);
        public void DrawText(Rect rect, string text, int font, float size, FontStyle style, TextAnchor anchor, bool wrap, Color color, Rect clip, bool rich) => inner.DrawText(rect, Mask(text), font, size, style, anchor, wrap, color, clip, false);

        public bool MouseOutsideHitClip => inner.MouseOutsideHitClip;
        public int GroupCount => inner.GroupCount;
        public bool InlineControlsObscured => inner.InlineControlsObscured;
        public float LineSpacing(int font, float size) => inner.LineSpacing(font, size);
        public void FontMetrics(int font, out float ascent, out float descent, out float lineGap) => inner.FontMetrics(font, out ascent, out descent, out lineGap);
        public int RegisterFont(string name, byte[] data, float size, int index) => inner.RegisterFont(name, data, size, index);
        public void DrawTexture(Rect rect, Texture texture, ScaleMode mode, Color color) => inner.DrawTexture(rect, texture, mode, color);
        public void DrawTextureWithTexCoords(Rect rect, Texture texture, Rect uv, Color color) => inner.DrawTextureWithTexCoords(rect, texture, uv, color);
        public void DrawTextureWithMaterial(Rect rect, Texture texture, Rect uv, Color color, Material material, Waylith.Rendering.Material native) => inner.DrawTextureWithMaterial(rect, texture, uv, color, material, native);
        public void FillRect(Rect rect, Color color) => inner.FillRect(rect, color);
        public void FillRectRounded(Rect rect, Color color, float radius) => inner.FillRectRounded(rect, color, radius);
        public void DrawTextureQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Texture texture, Color color) => inner.DrawTextureQuad(a, b, c, d, texture, color);
        public void DrawPrimitives(int mode, ReadOnlySpan<GuiVertex> vertices, Texture texture, bool wireframe) => inner.DrawPrimitives(mode, vertices, texture, wireframe);
        public void SetMatrix(Matrix4x4 matrix) => inner.SetMatrix(matrix);
        public void PushGroup(Rect rect) => inner.PushGroup(rect);
        public void PushScrollView(Rect rect, Vector2 scroll) => inner.PushScrollView(rect, scroll);
        public void PopGroup() => inner.PopGroup();
        public void GetGroup(int index, out Rect rect, out Vector2 offset) => inner.GetGroup(index, out rect, out offset);
        public void BeginWindowContent(Rect rect) => inner.BeginWindowContent(rect);
        public void EndWindowContent() => inner.EndWindowContent();
        public void PushClipRect(Rect rect) => inner.PushClipRect(rect);
        public void PopClipRect() => inner.PopClipRect();
        public void ActiveRenderTextureChanged(RenderTexture texture) => inner.ActiveRenderTextureChanged(texture);
        public void SetTextLanguage(string language) => inner.SetTextLanguage(language);
        public Vector2 ScreenToGuiPoint(Vector2 point) => inner.ScreenToGuiPoint(point);
        public Vector2 GuiToScreenPoint(Vector2 point) => inner.GuiToScreenPoint(point);
        public Vector2 GuiToDevicePoint(Vector2 point) => inner.GuiToDevicePoint(point);
    }
}
