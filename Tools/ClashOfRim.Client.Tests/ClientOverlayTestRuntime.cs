using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

// Managed overlay execution with rendering, input and translation replaced.
// Domain modules and component entry points are left intact.
internal sealed class ClientOverlayTestRuntime : IDisposable
{
    private static ClientOverlayTestRuntime? current;
    private readonly ClientOverlayTestRuntime? previous = current;
    private readonly ProgramState previousState = Current.ProgramState;
    private readonly Harmony harmony;
    internal readonly List<(Rect Rect, string Text)> Buttons = new();
    internal readonly List<string> Labels = new(), TextKeys = new();
    internal readonly List<Color> Colors = new();
    internal Func<string, bool> Click = _ => false;
    internal Func<string, string> Edit = value => value;
    internal Action<string>? Translating;
    internal bool WorldSelected;
    internal float Now;
    internal ClientOverlayTestRuntime(string name)
    {
        current = this; harmony = new(name + ".Overlay"); Current.ProgramState = ProgramState.Playing;
        void Prefix(System.Reflection.MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientOverlayTestRuntime), prefix));
        Prefix(AccessTools.PropertyGetter(typeof(WorldRendererUtility), nameof(WorldRendererUtility.WorldSelected)), nameof(Selected));
        Prefix(AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartup)), nameof(Clock));
        Prefix(AccessTools.PropertySetter(typeof(GUI), nameof(GUI.color)), nameof(Color));
        Prefix(AccessTools.PropertySetter(typeof(Text), nameof(Text.Font)), nameof(Skip));
        Prefix(AccessTools.PropertySetter(typeof(Text), nameof(Text.Anchor)), nameof(Skip));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClashOfRimText)).Where(m => m.Name == nameof(ClashOfRimText.Key))) Prefix(method, nameof(Translate));
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Widgets)))
        {
            string? prefix = method.Name switch
            {
                nameof(Widgets.Label) => nameof(Label), nameof(Widgets.ButtonText) => nameof(Button), nameof(Widgets.TextField) => nameof(Input),
                nameof(Widgets.DrawWindowBackground) or nameof(Widgets.BeginScrollView) or nameof(Widgets.EndScrollView) => nameof(Skip), _ => null
            };
            if (prefix is not null) Prefix(method, prefix);
        }
    }
    internal void Reset()
    {
        Buttons.Clear(); Labels.Clear(); TextKeys.Clear(); Colors.Clear();
        Click = _ => false; Edit = value => value; Translating = null; WorldSelected = false; Now = 0;
        Current.ProgramState = ProgramState.Playing;
    }
    public void Dispose() { harmony.UnpatchAll(harmony.Id); current = previous; Current.ProgramState = previousState; }
    private static bool Selected(ref bool __result) { __result = current!.WorldSelected; return false; }
    private static bool Clock(ref float __result) { __result = current!.Now; return false; }
    private static bool Color(UnityEngine.Color __0) { current!.Colors.Add(__0); return false; }
    private static bool Translate(string __0, ref string __result) { current!.TextKeys.Add(__0); current.Translating?.Invoke(__0); __result = __0; return false; }
    private static bool Label(object[] __args) { current!.Labels.Add(__args[1]?.ToString() ?? ""); return false; }
    private static bool Button(object[] __args, ref bool __result)
    { string text = __args[1]?.ToString() ?? ""; current!.Buttons.Add(((Rect)__args[0], text)); __result = current.Click(text); return false; }
    private static bool Input(string __1, ref string __result) { __result = current!.Edit(__1); return false; }
    private static bool Skip() => false;
}
