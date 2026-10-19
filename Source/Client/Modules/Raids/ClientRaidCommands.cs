using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal interface IClientRaidCommandSource
{
    bool IsCurrent { get; }
    bool IsAvailable { get; }
    int PawnCount { get; }
    IClientRaidLaunchRuntime CreateLaunch();
}
internal sealed record ClientRaidMenuOption(string Label, Action Choose);
internal interface IClientRaidCommandPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    string TargetLabel(ModWorldMapMarkerDto target);
    void Status(string message);
    void Message(string message, bool rejected);
    void Menu(IReadOnlyList<ClientRaidMenuOption> options);
    void Confirm(string message, Func<Task> confirmed);
}

// UI choices retain their original account, game/world and target. Actual force
// capture, admission, preparation and submission remain in ClientRaidLaunching.
internal sealed class ClientRaidCommands
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientRaidLaunching launching;
    private readonly IClientRaidCommandPresentation view;
    internal ClientRaidCommands(ClientSessionLifecycle session, ClientRaidLaunching launching, IClientRaidCommandPresentation view)
    { this.session = session; this.launching = launching; this.view = view; }

    private Func<Action, bool> Capture(IClientRaidCommandSource source)
    {
        var requests = new ClientRequestTracker<IClientRaidCommandSource>(session, () => source, s => s.IsCurrent, static () => { });
        var scope = requests.Capture();
        return action => requests.TryApply(scope, action);
    }
    internal void OpenCaravanMenu(IClientRaidCommandSource source, IReadOnlyList<ModWorldMapMarkerDto> targets)
    {
        using var p = ClientSessionWork.EnterPublication();
        var step = Capture(source);
        var frozen = ClientProtocolJson.Copy(targets.ToArray());
        if (!Enabled(step)) return;
        if (frozen.Length == 0) { Notice(step, "ClashOfRim.Raid.StatusNoTargets", rejected: false); return; }
        int count = 0;
        if (!step(() => count = source.IsAvailable ? source.PawnCount : 0)) return;
        if (count == 0) { Notice(step, "ClashOfRim.Raid.StatusNoPawns", rejected: false); return; }
        bool selected = false, confirmed = false;
        var options = new List<ClientRaidMenuOption>();
        foreach (var target in frozen)
        {
            string label = "", text = "";
            if (!step(() => label = view.TargetLabel(target))
                || !step(() => text = view.Text("ClashOfRim.Raid.CaravanTargetOption", ("TARGET", label), ("COUNT", count)))) return;
            options.Add(new(text, () => step(() =>
            {
                if (selected) return; selected = true;
                if (!Ready(step, source, target, vehicle: false)) return;
                string prompt = "";
                if (step(() => prompt = view.Text("ClashOfRim.Raid.ConfirmStart", ("TARGET", label), ("COUNT", count))))
                    step(() => view.Confirm(prompt, () =>
                    {
                        Task work = Task.CompletedTask;
                        step(() =>
                        {
                            if (confirmed) return; confirmed = true;
                            work = Start(step, source, target, vehicle: false);
                        });
                        return work;
                    }));
            })));
        }
        step(() => view.Menu(options));
    }
    internal Task StartVehicle(IClientRaidCommandSource source, ModWorldMapMarkerDto? target)
        => Start(Capture(source), source, target is null ? null : ClientProtocolJson.Copy(target), vehicle: true);
    internal Task StartTransporters(IClientRaidCommandSource source, ModWorldMapMarkerDto target)
        => Start(Capture(source), source, ClientProtocolJson.Copy(target), vehicle: false);
    private Task Start(Func<Action, bool> step, IClientRaidCommandSource source, ModWorldMapMarkerDto? target, bool vehicle)
    {
        if (!Ready(step, source, target, vehicle)) return Task.CompletedTask;
        IClientRaidLaunchRuntime? runtime = null;
        if (!step(() => runtime = source.CreateLaunch())) return Task.CompletedTask;
        Task work = Task.CompletedTask;
        step(() => work = launching.StartAsync(runtime!, target));
        return work;
    }
    private bool Ready(Func<Action, bool> step, IClientRaidCommandSource source, ModWorldMapMarkerDto? target, bool vehicle)
    {
        if (!Enabled(step)) return false;
        bool available = false;
        if (!step(() => available = source.IsAvailable)) return false;
        if (!available)
        {
            Notice(step, vehicle ? "ClashOfRim.VehicleFramework.RemoteLanding.RaidStartFailedNoCaravan" : "ClashOfRim.Raid.StatusAttackerPawnUnavailable");
            return false;
        }
        if (!ClientRaidTargets.IsRaidable(target))
        {
            Notice(step, vehicle ? "ClashOfRim.VehicleFramework.RemoteLanding.RaidUnavailable" : "ClashOfRim.Raid.StatusTargetIncomplete");
            return false;
        }
        return true;
    }
    private bool Enabled(Func<Action, bool> step)
    {
        bool enabled = false;
        if (!step(() => enabled = launching.Enabled)) return false;
        if (!enabled) Notice(step, "ClashOfRim.Raid.PvpDisabled");
        return enabled;
    }
    private void Notice(Func<Action, bool> step, string key, bool rejected = true)
    {
        string text = "";
        if (!step(() => text = view.Text(key))) return;
        step(() => view.Status(text));
        if (rejected) step(() => view.Message(text, true));
    }
    internal static ModWorldMapMarkerDto TransporterTarget(string? user, string? colony, string? snapshot, string? map,
        string? worldObject, int tile, int layer, string? label) => new()
    {
        MarkerId = "", Kind = "TradeableColony", OwnerUserId = user ?? "", OwnerColonyId = colony ?? "", SnapshotId = snapshot ?? "",
        MapId = map ?? "", WorldObjectId = worldObject ?? "", Tile = tile, TileLayerId = Math.Max(0, layer), Label = label ?? "", CanRaid = true
    };
}
