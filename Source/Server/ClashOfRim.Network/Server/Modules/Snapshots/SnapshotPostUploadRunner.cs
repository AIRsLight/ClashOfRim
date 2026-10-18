using AIRsLight.ClashOfRim.Network.Plugins;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class SnapshotPostUploadRunner(SnapshotPostUploadJobRegistry jobs, Func<ILogger> logger)
{
    internal void Run(SnapshotPostUploadInput input, IEnumerable<SnapshotPostUploadOperation> operations)
    {
        var registered = operations.ToList();
        ValidateRegistrations(registered.Select(operation => operation.Processor).ToArray());
        var applicable = registered.Where(operation => operation.Processor.Supports(input.Kind)).ToList();
        foreach (var operation in applicable.OrderBy(operation => operation.Processor.Stage)
            .ThenBy(operation => operation.Processor.Order)
            .ThenBy(operation => operation.Processor.Id, StringComparer.Ordinal))
            Execute(input, operation);
    }

    internal static void ValidateRegistrations(IReadOnlyCollection<ISnapshotPostUploadProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        if (processors.Any(processor => string.IsNullOrWhiteSpace(processor.Id)))
        {
            throw new InvalidOperationException("Snapshot post-upload processor IDs cannot be empty.");
        }

        string? duplicateId = processors
            .GroupBy(processor => processor.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (duplicateId is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate snapshot post-upload processor ID '{duplicateId}'. Processor IDs must be globally unique.");
        }
    }

    private void Execute(SnapshotPostUploadInput input, SnapshotPostUploadOperation operation)
    {
        var processor = operation.Processor;
        try
        {
            if (processor.ExecutionMode == SnapshotPostUploadExecutionMode.Deferred)
            {
                if (operation.Schedule is { } schedule)
                {
                    schedule(input);
                }
                else
                {
                    if (operation.CapturePayload is null)
                    {
                        throw new InvalidOperationException(
                            $"Deferred snapshot post-upload processor '{processor.Id}' does not implement {nameof(IDeferredSnapshotPostUploadProcessor)}.");
                    }
                    string payloadJson = operation.CapturePayload(input);
                    jobs.Enqueue(processor.Id, input.Job, payloadJson);
                }
            }
            else
            {
                if (operation.Process is null)
                {
                    throw new InvalidOperationException(
                        $"Inline snapshot post-upload processor '{processor.Id}' does not implement {nameof(IInlineSnapshotPostUploadProcessor)}.");
                }

                operation.Process(input);
            }
        }
        catch (Exception ex)
        {
            logger().LogError(
                ex,
                "Snapshot post-upload processor failed: processor={ProcessorId} stage={Stage} kind={Kind} snapshot={SnapshotId}",
                processor.Id,
                processor.Stage,
                input.Kind,
                input.Snapshot?.Identity.SnapshotId);
            if (processor.FailureMode == SnapshotPostUploadFailureMode.AbortPipeline)
            {
                throw;
            }
        }
    }
}
