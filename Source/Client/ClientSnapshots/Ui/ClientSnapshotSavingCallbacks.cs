using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.World;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class ClientSnapshotSavingCallbacks
{
    internal static ClientSnapshotSavingCallbacks? Current { get; private set; }
    internal static void Attach(ClientSnapshotSavingCallbacks? value) => Current = value;
    private readonly ClientSnapshotSaving saving;
    internal ClientSnapshotSavingCallbacks(ClientSnapshotSaving saving) => this.saving = saving;
    internal Func<Action?, Task>? PrepareMenu()
    {
        if (!ReferenceEquals(Current, this)) return null;
        var command = saving.PrepareMenu();
        if (command is null) return null;
        return exit =>
        {
            if (!ReferenceEquals(Current, this) || !command.IsCurrent) return Task.CompletedTask;
            Find.MainTabsRoot?.SetCurrentTab(null);
            return ReferenceEquals(Current, this) ? command.StartAsync(exit) : Task.CompletedTask;
        };
    }
    internal bool TryAutosave()
    {
        if (!ReferenceEquals(Current, this)) return false;
        return saving.TryAutosave(out _);
    }
}
