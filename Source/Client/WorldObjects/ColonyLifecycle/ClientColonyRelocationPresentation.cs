using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.World;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientColonyRelocationPresentation : ClientWorldWritePresentation, IClientColonyRelocationPresentation
{
    internal ClientColonyRelocationPresentation() : base(static _ => { }) { }
    public void Confirm(string colony, int tile, Func<Task> confirm)
        => Find.WindowStack.Add(new ColonyRelocationConfirmationWindow(colony, tile, () => _ = confirm()));
    public string Blocked(LocalMutationState mutation) => ClientLocalMutationPresentation.Blocked(mutation);
    public string SnapshotFailure(SnapshotUploadFailure failure) => ClientSnapshotUploadPresentation.ToPublicResult(new(null, null, failure)).Message ?? "";
}
