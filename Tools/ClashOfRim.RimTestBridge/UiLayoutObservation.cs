using HarmonyLib;
using UnityEngine;
using Verse;

namespace ClashOfRim.RimTestBridge;

// Test-only observation of real repaint calls; no replacement renderer or font metrics.
internal static class UiLayoutObservation
{
    internal static Window? Target;
    internal static int Frame;
    internal static object[] Labels = [];
    private static bool recording;
    private static readonly List<object> labels = new();
    private static bool installed;
    internal static void Install()
    {
        if (installed) return;
        installed = true;
        var harmony = new Harmony("ClashOfRim.RimTest.UiLayout");
        harmony.Patch(AccessTools.Method(typeof(Window), "InnerWindowOnGUI"),
            prefix: new HarmonyMethod(typeof(UiLayoutObservation), nameof(Begin)),
            finalizer: new HarmonyMethod(typeof(UiLayoutObservation), nameof(End)));
        harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.Label), [typeof(Rect), typeof(string)]),
            prefix: new HarmonyMethod(typeof(UiLayoutObservation), nameof(Label)));
    }
    private static void Begin(Window __instance)
    {
        recording = ReferenceEquals(__instance, Target) && Event.current.type == EventType.Repaint;
        if (recording) labels.Clear();
    }
    private static void End(Window __instance)
    {
        if (recording && ReferenceEquals(__instance, Target)) { Labels = labels.ToArray(); Frame++; }
        recording = false;
    }
    private static void Label(Rect __0, string __1)
    {
        if (!recording || string.IsNullOrWhiteSpace(__1)) return;
        Rect position = __0; var content = new GUIContent(__1); GUIStyle style = Text.CurFontStyle;
        var size = style.CalcSize(content);
        float height = style.wordWrap ? style.CalcHeight(content, position.width) : size.y;
        var point = GUIUtility.GUIToScreenPoint(position.position);
        var end = GUIUtility.GUIToScreenPoint(position.max);
        labels.Add(new { text = content.text, rect = Bounds(position), screen = Bounds(new Rect(point, end - point)),
            width = size.x, height, wrap = style.wordWrap, font = Text.Font.ToString(),
            overflow = position.width <= 0 || position.height <= 0 || height > position.height + 2
                || !style.wordWrap && size.x > position.width + 2 });
    }
    internal static object Bounds(Rect rect) => new { x = rect.x, y = rect.y, width = rect.width, height = rect.height };
}
