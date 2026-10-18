using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

internal sealed class AdminConfigurationChanges(ServerConfigurationState runtime, ServerConfigurationRegistry settings,
    CompatibilityBaselineRegistry compatibility, AdminControlRegistry control, SnapshotMutationCoordinator mutations)
{
    internal AdminConfigurationDto Current => AdminConfigurationMapping.ToDto(runtime.Current, compatibility.Current);

    internal AdminConfigurationDto PublishUnderMutation(AdminConfigurationDto requested, string actor, DateTimeOffset at)
    {
        var candidate = AdminConfigurationMapping.Apply(requested, runtime.Current);
        var manifest = AdminConfigurationMapping.ApplyRoles(compatibility.Current, requested);
        var dto = AdminConfigurationMapping.ToDto(candidate, manifest);
        settings.Stage(new(1, dto, manifest, actor, at));
        CompletePublication();
        return dto;
    }

    internal void Recover()
    {
        using var mutation = mutations.EnterUnrecovered();
        CompletePublication();
    }

    private void CompletePublication()
    {
        if (settings.Pending is not { } work) return;
        // Reconstruct against current non-admin settings; secrets and authentication are not persisted in the DTO.
        var candidate = AdminConfigurationMapping.Apply(work.Configuration, runtime.Current);
        if (work.CompatibilityBaseline is { } manifest)
            compatibility.ReplaceBaseline(manifest, work.ActorUserId, work.CreatedAtUtc);
        control.EnsureAudit(new("UpdateConfiguration", work.ActorUserId, null, null, work.CreatedAtUtc));
        settings.Complete(work);
        runtime.Replace(candidate);
    }
}
