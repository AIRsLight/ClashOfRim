using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Multiplayer.Players;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Diplomacy;

internal sealed class PlayerFactionColonyNavigation
{
    internal static PlayerFactionColonyNavigation? Current { get; private set; }
    internal static void Attach(PlayerFactionColonyNavigation? value) => Current = value;
    private readonly Func<bool> isActive;
    private readonly PlayerColonyLocator locations;

    internal PlayerFactionColonyNavigation(Func<bool> isActive, PlayerColonyLocator locations)
    { this.isActive = isActive; this.locations = locations; }

    internal bool TryLocate(Faction faction)
    {
        if (!isActive() || !PlayerFactionProxyUtility.IsServerPlayerProxy(faction)) return false;
        string owner = PlayerFactionProxyUtility.ProxyOwnerUserId(faction);
        if (string.IsNullOrWhiteSpace(owner)) return false;

        // Factions belong to accounts. Resolve their current colony from the same
        // live world markers as the multiplayer page, without parsing display names.
        locations.Reset();
        if (locations.Locate(new ModPlayerSummaryDto { UserId = owner }, playerVersion: -1))
            Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
        return true;
    }
}

[HarmonyPatch(typeof(FactionUIUtility), "DrawFactionRow")]
internal static class PlayerFactionRowNavigationPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var button = AccessTools.Method(typeof(Widgets), nameof(Widgets.ButtonInvisible), new[] { typeof(Rect), typeof(bool) });
        var calls = code.Select((instruction, index) => (instruction, index)).Where(pair => pair.instruction.Calls(button)).ToList();
        if (calls.Count != 1)
            throw new InvalidOperationException("Player faction navigation requires the native faction-name button.");

        var (call, offset) = calls[0];
        // Keep the native hit area, layout and separate info/ideology buttons.
        code.Insert(offset, new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(call));
        call.operand = AccessTools.Method(typeof(PlayerFactionRowNavigationPatch), nameof(FactionButton));
        return code;
    }

    internal static bool FactionButton(Rect rect, bool doMouseoverSound, Faction faction)
    {
        if (!Widgets.ButtonInvisible(rect, doMouseoverSound)) return false;
        // A handled player click suppresses only the original name-click info card.
        return PlayerFactionColonyNavigation.Current?.TryLocate(faction) != true;
    }
}
