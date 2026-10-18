using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionStartupPresentation : IClientSessionStartupPresentation
{
    private readonly Action<string> login, snapshot;
    private readonly Action<ModLoginResponseDto> mismatch;
    internal ClientSessionStartupPresentation(Action<string> login, Action<string> snapshot, Action<ModLoginResponseDto> mismatch)
    { this.login = login; this.snapshot = snapshot; this.mismatch = mismatch; }
    public string Text(string key, params (string Name, object? Value)[] args) => ClashOfRimText.Key(key, args.Select(a => a.Value.Named(a.Name)).ToArray());
    public string Admission(LocalMutationAdmission admission, LocalMutationState? mutation) => ClientLocalMutationPresentation.AdmissionFailure(admission, mutation);
    public string SnapshotFailure(SnapshotUploadFailure failure) => ClientSnapshotUploadPresentation.ToPublicResult(new(null, null, failure)).Message ?? "";
    public void LoginStatus(string text) => login(text);
    public void SnapshotStatus(string text) => snapshot(text);
    public void Message(string text) => Messages.Message(text, MessageTypeDefOf.PositiveEvent, historical: false);
    public void CompatibilityMismatch(ModLoginResponseDto response) => mismatch(response);
    public void Diagnose(string text) => Log.Warning("[ClashOfRim] " + text);
}
