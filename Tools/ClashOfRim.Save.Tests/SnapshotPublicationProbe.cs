using AIRsLight.ClashOfRim.Network.Plugins;

internal sealed class SnapshotPublicationProbe(string id, string userId, Action run) : IInlineSnapshotPostUploadProcessor
{
    public string Id => id;
    public int Order => 350;
    public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
    public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
    public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
    public bool Supports(SnapshotPostUploadKind kind) => true;
    public void Process(SnapshotPostUploadContext context) { if (context.UserId == userId) run(); }
}
