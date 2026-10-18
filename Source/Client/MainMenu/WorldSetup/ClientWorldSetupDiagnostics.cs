using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

internal static class ClientWorldSetupDiagnostics
{
    internal static void Log(string stage, string? detail = null)
        => ClashLog.Message("[ClashOfRim.Flow] " + stage + (string.IsNullOrWhiteSpace(detail) ? "" : " | " + detail));

    internal static string DescribeWindow(Window? window)
    {
        if (window is null) return "<null>";
        string result = window.GetType().Name;
        if (window is Page page)
            result += $"(prev={page.prev?.GetType().Name ?? "<null>"}, next={page.next?.GetType().Name ?? "<null>"}, nextAct={DescribeAction(page.nextAct)})";
        return result + $"[absorb={window.absorbInputAroundWindow}, preventCamera={window.preventCameraMotion}, layer={window.layer}]";
    }
    internal static string DescribeWindowStack()
    {
        if (Find.WindowStack is null) return "WindowStack=<null>";
        List<string> windows = new();
        for (int i = 0; i < Find.WindowStack.Count; i++) windows.Add(i + ":" + DescribeWindow(Find.WindowStack[i]));
        return "WindowStack.Count=" + Find.WindowStack.Count + " [" + string.Join("; ", windows) + "]";
    }
    internal static string DescribeAction(Action? action) => action is null ? "<null>" : action.Method.DeclaringType?.Name + "." + action.Method.Name;
}
