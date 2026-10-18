using System;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class ClientSnapshotSavingPresentation : IClientSnapshotSavingPresentation
{
    private readonly Action<string> status;
    internal ClientSnapshotSavingPresentation(Action<string> status) => this.status = status;
    public string Format(ClientSnapshotSaveNotice notice)
    {
        if (notice.Stage == ClientSnapshotSaveStage.Blocked)
        {
            string reason = ClientLocalMutationPresentation.Blocked(notice.Mutation!);
            return notice.Kind == ClientSnapshotSaveKind.Autosave
                ? ClashOfRimText.Key("ClashOfRim.AutosaveSnapshot.SkippedAtomic", reason.Named("REASON")) : reason;
        }
        string prefix = notice.Kind switch
        {
            ClientSnapshotSaveKind.Manual => "ClashOfRim.SnapshotUpload.",
            ClientSnapshotSaveKind.Menu => "ClashOfRim.Menu.UploadSnapshot",
            ClientSnapshotSaveKind.Autosave => "ClashOfRim.AutosaveSnapshot.",
            _ => throw new ArgumentOutOfRangeException(nameof(notice))
        };
        string stage = notice.Kind == ClientSnapshotSaveKind.Autosave && notice.Stage is ClientSnapshotSaveStage.NoSession or ClientSnapshotSaveStage.Busy
            ? "Skipped" + notice.Stage : notice.Stage.ToString();
        string detail = notice.Failure is { } failure ? ClientSnapshotUploadPresentation.ToPublicResult(new(null, null, failure)).Message ?? "" : "";
        return ClashOfRimText.Key(prefix + stage, (notice.Snapshot ?? "").Named("SNAPSHOT"), (notice.Failure?.Code ?? "").Named("CODE"),
            (notice.Exception?.GetType().Name ?? "").Named("TYPE"), (notice.Exception?.Message ?? detail).Named("MESSAGE"));
    }
    public void Status(string message) => status(message);
    public void Message(string message, bool success) => Messages.Message(message,
        success ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.RejectInput, historical: false);
    public void Diagnose(ClientSnapshotSaveNotice notice, string message)
    {
        string detail = "[ClashOfRim] " + notice.Kind + " snapshot " + notice.Stage + ": " + message;
        if (notice.Exception is not null) detail += " | " + notice.Exception;
        if (notice.Stage is ClientSnapshotSaveStage.Failed or ClientSnapshotSaveStage.Exception) Log.Warning(detail);
        else ClashLog.Message(detail);
    }
}
