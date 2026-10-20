using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Diplomacy;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

// One application owns a snapshot of the reference graph. Materialization must
// not change that graph while later catalog entries are still choosing objects.
internal sealed class ServerFactionIdeologyBindings
{
    private sealed record Slot(Faction Faction, int Index)
    {
        internal Ideo? Read() => Index == 0 ? Faction.ideos?.PrimaryIdeo
            : Faction.ideos?.IdeosMinorListForReading.ElementAtOrDefault(Index - 1);
    }
    private sealed record Binding(Slot Slot, string Key, Ideo? Previous);
    private readonly List<Binding> bindings = new();
    private readonly Dictionary<string, Ideo> resolved = new(StringComparer.Ordinal);
    private readonly HashSet<Faction> completeTrackers = new();
    private readonly Faction[] factions;

    private static IEnumerable<Faction> NpcFactions() => (Find.FactionManager?.AllFactionsListForReading
        ?? new List<Faction>()).Where(f => !f.IsPlayer && !PlayerFactionProxyUtility.IsServerPlayerProxy(f));

    internal static List<ModWorldFactionIdeoBindingDto> Capture(Ideo ideo) =>
        NpcFactions().GroupBy(f => f.def.defName, StringComparer.Ordinal)
            .SelectMany(group => group.OrderBy(f => f.loadID).SelectMany((f, factionIndex) =>
                Slots(f).Where(slot => ReferenceEquals(slot.Read(), ideo)).Select(slot => new ModWorldFactionIdeoBindingDto
                { FactionDefName = group.Key, FactionIndex = factionIndex, IdeoIndex = slot.Index }))).ToList();

    internal ServerFactionIdeologyBindings(IEnumerable<ModWorldIdeoSummaryDto> catalog, bool completeCatalog = false)
    {
        factions = (Find.FactionManager?.AllFactionsListForReading ?? new List<Faction>()).ToArray();
        var npc = NpcFactions().GroupBy(f => f.def.defName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.loadID).ToArray(), StringComparer.Ordinal);
        foreach (var dto in catalog.Where(d => string.Equals(d.OwnerUserId, "server", StringComparison.OrdinalIgnoreCase)))
        {
            var refs = dto.FactionBindings;
            if (refs is null)
            {
                // Old catalogs cannot identify a particular instance of a repeated
                // faction Def. Refuse an arbitrary FirstOrDefault assignment.
                if (dto.FactionDefName is null || !npc.TryGetValue(dto.FactionDefName, out var legacy)) continue;
                if (legacy.Length != 1) throw new InvalidOperationException("Ambiguous legacy ideology faction: " + dto.FactionDefName);
                refs = new() { new() { FactionDefName = dto.FactionDefName } };
            }
            foreach (var reference in refs)
            {
                if (reference.FactionIndex < 0 || reference.IdeoIndex < 0)
                    throw new InvalidOperationException("Invalid faction ideology index: " + dto.GlobalKey);
                if (!npc.TryGetValue(reference.FactionDefName, out var matches)
                    || reference.FactionIndex >= matches.Length)
                    throw new InvalidOperationException("Missing ideology faction: " + reference.FactionDefName);
                var slot = new Slot(matches[reference.FactionIndex], reference.IdeoIndex);
                if (completeCatalog && dto.FactionBindings is not null) completeTrackers.Add(slot.Faction);
                if (!completeCatalog && slot.Index > 1 + (slot.Faction.ideos?.IdeosMinorListForReading.Count ?? 0))
                    throw new InvalidOperationException("Incomplete minor ideology slots: " + dto.GlobalKey);
                if (bindings.Any(b => b.Slot == slot))
                    throw new InvalidOperationException("Duplicate faction ideology slot: " + dto.GlobalKey);
                bindings.Add(new(slot, dto.GlobalKey, slot.Read()));
            }
        }
    }

    internal bool HasBindings(ModWorldIdeoSummaryDto dto) => bindings.Any(b => b.Key == dto.GlobalKey);

    internal Ideo? FindReusable(ModWorldIdeoSummaryDto dto)
    {
        RemoteIdeoCatalog.TryGetIdeo(dto.GlobalKey, out var registered);
        return bindings.Where(b => b.Key == dto.GlobalKey).Select(b => b.Previous).Append(registered)
            .Where(i => i is not null).Cast<Ideo>().Distinct()
            .FirstOrDefault(i => !i.initialPlayerIdeo
                && RemoteIdeoCatalog.IsInCurrentIdeoManager(i)
                && RemoteIdeoCatalog.GetGlobalKeys(i).All(key => key == dto.GlobalKey)
                && factions.SelectMany(Slots).Where(slot => ReferenceEquals(slot.Read(), i))
                    .All(slot => bindings.Any(b => b.Slot == slot && b.Key == dto.GlobalKey)));
    }

    internal void Resolve(ModWorldIdeoSummaryDto dto, Ideo ideo) => resolved.Add(dto.GlobalKey, ideo);

    internal void Apply()
    {
        var ready = bindings.Where(b => resolved.ContainsKey(b.Key)).ToArray();
        // Every slot of an affected tracker must be ready before changing it.
        var groups = ready.GroupBy(b => b.Slot.Faction).Where(g =>
            bindings.Where(b => b.Slot.Faction == g.Key).All(b => resolved.ContainsKey(b.Key))).ToArray();
        var pawns = !groups.SelectMany(g => g).Any(b => b.Previous is not null && !ReferenceEquals(b.Previous, resolved[b.Key])) ? Array.Empty<Pawn>()
            : PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.ToArray();
        foreach (var group in groups)
        {
            var tracker = group.Key.ideos ??= new FactionIdeosTracker(group.Key);
            foreach (var binding in group.Where(b => b.Slot.Index == 0))
                tracker.SetPrimary(resolved[binding.Key]);
            var minor = group.Where(b => b.Slot.Index > 0).OrderBy(b => b.Slot.Index).ToArray();
            if (completeTrackers.Contains(group.Key))
            {
                tracker.IdeosMinorListForReading.Clear();
                tracker.IdeosMinorListForReading.AddRange(minor.Select(b => resolved[b.Key]).Distinct());
            }
            else foreach (var binding in minor)
            {
                int index = binding.Slot.Index - 1;
                if (index == tracker.IdeosMinorListForReading.Count)
                    tracker.IdeosMinorListForReading.Add(resolved[binding.Key]);
                else tracker.IdeosMinorListForReading[index] = resolved[binding.Key];
            }
            foreach (var pawn in pawns.Where(p => p.Faction == group.Key && p.ideo?.Ideo is not null))
            {
                var replacements = group.Where(b => ReferenceEquals(b.Previous, pawn.ideo.Ideo))
                    .Select(b => resolved[b.Key]).Distinct().ToArray();
                if (replacements.Length == 1 && !ReferenceEquals(pawn.ideo.Ideo, replacements[0]))
                    RebindPawn(pawn.ideo, replacements[0]);
            }
        }
    }

    private static IEnumerable<Slot> Slots(Faction faction) =>
        Enumerable.Range(0, 1 + (faction.ideos?.IdeosMinorListForReading.Count ?? 0)).Select(i => new Slot(faction, i));

    private static readonly AccessTools.FieldRef<Pawn_IdeoTracker, Ideo> PawnIdeo =
        AccessTools.FieldRefAccess<Pawn_IdeoTracker, Ideo>("ideo");
    private static readonly Action<Pawn_IdeoTracker> RecachePawn =
        AccessTools.MethodDelegate<Action<Pawn_IdeoTracker>>(AccessTools.Method(typeof(Pawn_IdeoTracker), "RecacheIdeoComponents"));

    internal static void RebindPawn(Pawn_IdeoTracker tracker, Ideo ideo)
    {
        // Synchronization repairs identity, not gameplay conversion. Keep certainty,
        // previous ideologies, roles and history; refresh only derived components.
        PawnIdeo(tracker) = ideo;
        RecachePawn(tracker);
    }
}
