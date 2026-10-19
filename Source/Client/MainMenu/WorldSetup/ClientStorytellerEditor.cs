using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

internal sealed class ClientStorytellerEditor
{
    internal static ClientStorytellerEditor? Current { get; private set; }
    internal static void Attach(ClientStorytellerEditor? value) => Current = value;
    private readonly ClientWorldWrites writes;
    private readonly ClientColonyAppearance appearance;
    private readonly ConditionalWeakTable<Window, ClientWorldWrites.StorytellerEdit> edits = new();
    internal ClientStorytellerEditor(ClientWorldWrites writes, ClientColonyAppearance appearance)
    { this.writes = writes; this.appearance = appearance; }

    internal bool Opening(Window window)
    {
        if (window is not Page_SelectStorytellerInGame) return true;
        if (edits.TryGetValue(window, out var existing)) return existing.IsCurrent;
        if (!writes.OpenStorytellerEdit(out var edit)) return false;
        if (edit is not null) edits.Add(window, edit);
        return true;
    }
    internal async Task ClosedAsync(Window window)
    {
        if (!edits.TryGetValue(window, out var edit)) return;
        edits.Remove(window);
        if (!ReferenceEquals(Current, this)) return;
        try { await edit.CloseAsync(appearance.Capture).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warning("[ClashOfRim] Failed to submit storyteller page changes: " + ex); }
    }
}
