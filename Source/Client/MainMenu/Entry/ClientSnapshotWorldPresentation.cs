using System;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientSnapshotWorldPresentation : IClientSnapshotWorldPresentation
{
    private readonly Action<string> status;
    internal ClientSnapshotWorldPresentation(Action<string> status) => this.status = status;
    public string RaidRestored(string snapshotId) => ClashOfRimText.Key("ClashOfRim.Raid.StatusCleanupSnapshotSucceeded", snapshotId.Named("SNAPSHOT"));
    public void Status(string message) => status(message);
    public void Message(string message) => Messages.Message(message, MessageTypeDefOf.NeutralEvent, historical: false);
}
