using AIRsLight.ClashOfRim.Network.Plugins;

internal sealed class SnapshotPublicationProbe(string id, int order, Func<SnapshotPostUploadKind, bool> supports,
    Action<SnapshotPostUploadContext> process) : IInlineSnapshotPostUploadProcessor
{
    public SnapshotPublicationProbe(string id, string userId, Action run)
        : this(id, 350, _ => true, context => { if (context.UserId == userId) run(); }) { }

    public string Id => id;
    public int Order => order;
    public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
    public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
    public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
    public bool Supports(SnapshotPostUploadKind kind) => supports(kind);
    public void Process(SnapshotPostUploadContext context) => process(context);
}
