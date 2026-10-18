using RimWorld;
using System;
using System.Collections.Generic;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;

// Owns only newly generated placeholders until the whole item has been applied.
internal sealed class BiotechPreparedSources : IDisposable
{
    private readonly CompHasPawnSources comp;
    private readonly IReadOnlyList<BiotechSourcePawnRestorer.PreparedPawn> sources;
    private readonly string? currentUserId;
    private bool applied;

    internal BiotechPreparedSources(CompHasPawnSources comp,
        IReadOnlyList<BiotechSourcePawnRestorer.PreparedPawn> sources, string? currentUserId)
    {
        this.comp = comp;
        this.sources = sources;
        this.currentUserId = currentUserId;
    }

    internal bool TryCreatePawns(out string? missingDefName)
    {
        missingDefName = null;
        bool ready = false;
        try
        {
            foreach (var source in sources)
                if (!source.TryCreate(currentUserId, out missingDefName)) return false;
            ready = true;
            return true;
        }
        finally
        {
            if (!ready) Dispose();
        }
    }

    internal void Apply()
    {
        foreach (var source in sources) source.Apply(currentUserId);
        ClearExistingPawnSources();
        foreach (var source in sources) comp.AddSource(source.Pawn!);
        applied = true;
    }

    private void ClearExistingPawnSources()
    {
        if (comp.pawnSources is null || comp.pawnSources.Count == 0)
        {
            comp.pawnSources = new List<Pawn>();
            return;
        }
        try
        {
            Find.WorldPawns?.RemovePawnSources(comp.pawnSources, comp);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Biotech] Failed to unlink old reproductive source references: " + ex.Message);
        }
        comp.pawnSources.Clear();
    }

    public void Dispose()
    {
        if (applied) return;
        foreach (var source in sources) source.DiscardCreatedPawn();
    }
}
