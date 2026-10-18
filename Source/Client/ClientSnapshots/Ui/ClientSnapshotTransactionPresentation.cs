using System;
using System.Globalization;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class ClientSnapshotTransactionPresentation : IClientSnapshotTransactionPresentation
{
    public string Uploading => ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Uploading");
    public bool Playing => Current.ProgramState == ProgramState.Playing;
    public void Enqueue(Action action, Action discarded) => ClientMainThreadDispatcher.Enqueue(action, discarded);
    public void Message(string message) => Messages.Message(message, MessageTypeDefOf.NeutralEvent, historical: false);
    public void Log(SnapshotTransactionNotice notice)
    {
        string message = Describe(notice);
        if (notice.Phase == SnapshotTransactionPhase.Rejected) Verse.Log.Warning(message);
        else ClashLog.Message(message);
    }
    internal static string Describe(SnapshotTransactionNotice notice)
    {
        string owner = string.IsNullOrWhiteSpace(notice.Owner) ? "<none>" : notice.Owner;
        string age = notice.Age is { } elapsed ? elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s" : "n/a";
        string sequence = notice.Sequence.ToString(CultureInfo.InvariantCulture);
        return notice.Phase switch
        {
            SnapshotTransactionPhase.Rejected => "[ClashOfRim][SnapshotTxn] begin rejected: requested=" + notice.RequestedOwner
                + ", snapshotUploadInProgress=" + notice.SnapshotInProgress + ", manualSyncInProgress=" + notice.ManualInProgress
                + ", owner=" + owner + ", seq=" + sequence + ", age=" + age,
            SnapshotTransactionPhase.Started => "[ClashOfRim][SnapshotTxn] begin: owner=" + owner + ", seq=" + sequence + ".",
            SnapshotTransactionPhase.Ended => "[ClashOfRim][SnapshotTxn] end: owner=" + owner + ", seq=" + sequence
                + ", snapshotUploadInProgress=" + notice.SnapshotInProgress + ", manualSyncInProgress=" + notice.ManualInProgress + ", age=" + age + ".",
            _ => throw new ArgumentOutOfRangeException(nameof(notice))
        };
    }
}
