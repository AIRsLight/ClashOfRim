using RimTest.Protocol;
using RimWorld;
using UnityEngine;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal sealed partial class DomainCommands
{
    private void RegisterAccountLifecycle()
    {
        bridge.Router.Register("clash.abandon", "Observe the real abandonment confirmation and its ten second countdown.", "observe", _ =>
        {
            var window = AbandonWindow();
            return new { open = window is not null, ready = window is not null && Time.realtimeSinceStartup - Field<float>(window, "openedAtRealtime") >= 10f };
        });
        bridge.Router.Register("clash.abandon.open", "Open the current home colony's original abandonment confirmation.", "semantic", _ =>
        {
            Loopback(); var map = Find.CurrentMap;
            if (map?.IsPlayerHome != true) throw new CommandException("invalid_context", "Expected current home map.");
            Invoke(Mod, "OpenAbandonPlayerColonyConfirmation", map.Parent); return new { invoked = true };
        });
        bridge.Router.Register("clash.abandon.choose", "Cancel or confirm the visible abandonment dialog, respecting its countdown and original action.", "semantic", a =>
        {
            Loopback(); var window = AbandonWindow() ?? throw new CommandException("invalid_context", "No abandonment dialog.");
            bool confirm = a.Bool("confirm");
            if (confirm && Time.realtimeSinceStartup - Field<float>(window, "openedAtRealtime") < 10f)
                throw new CommandException("disabled", "Abandonment countdown has not elapsed.");
            var colony = Field<RimWorld.Planet.MapParent>(window, "colony");
            window.Close(); if (confirm) Invoke(Mod, "StartAbandonPlayerColony", colony);
            return new { invoked = true, confirm };
        });
        bridge.Router.Register("clash.achievement.version", "Observe achievement publication version for deterministic refresh completion.", "observe", _ =>
            new { version = Field<int>(Mod, "achievementLeaderboardsSnapshotVersion") });
    }
    private static Window? AbandonWindow() => Find.WindowStack.Windows.SingleOrDefault(w => w.GetType().Name == "AbandonPlayerColonyConfirmationWindow");
}
