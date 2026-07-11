using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class RemoteIdeoDiagnostics
{
    internal static void LogCatalogState(
        IReadOnlyList<ModWorldIdeoSummaryDto> serverIdeos,
        string? localUserId)
    {
        if (!Prefs.DevMode || Find.IdeoManager?.IdeosListForReading is not { } localIdeos)
        {
            return;
        }

        List<Faction> factions = Find.FactionManager?.AllFactionsListForReading?
            .Where(faction => faction is not null)
            .ToList() ?? new List<Faction>();
        Dictionary<Ideo, int> pawnReferences = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead
            .Where(pawn => pawn?.ideo?.Ideo is not null)
            .GroupBy(pawn => pawn.ideo.Ideo)
            .ToDictionary(group => group.Key, group => group.Count());

        var builder = new StringBuilder();
        builder.AppendLine("[ClashOfRim][IdeoAudit] catalog application completed")
            .Append("localUser=").Append(localUserId ?? "<null>")
            .Append(" serverEntries=").Append(serverIdeos.Count)
            .Append(" localIdeos=").Append(localIdeos.Count)
            .AppendLine();

        foreach (ModWorldIdeoSummaryDto dto in serverIdeos
            .OrderBy(ideo => ideo.GlobalKey, StringComparer.Ordinal))
        {
            builder.Append("catalog key=").Append(Display(dto.GlobalKey))
                .Append(" owner=").Append(Display(dto.OwnerUserId))
                .Append(" localId=").Append(Display(dto.LocalId))
                .Append(" name=").Append(Display(dto.Name))
                .Append(" factionDef=").Append(Display(dto.FactionDefName))
                .Append(" initial=").Append(dto.InitialPlayerIdeo)
                .Append(" package=").Append(ShortHash(dto.SavedIdeoPackageSha256))
                .AppendLine();
        }

        foreach (Ideo ideo in localIdeos
            .Where(ideo => ideo is not null)
            .OrderBy(ideo => ideo.id))
        {
            IReadOnlyList<string> globalKeys = RemoteIdeoCatalog.GetGlobalKeys(ideo);
            List<string> factionReferences = factions
                .Where(faction => faction.ideos?.AllIdeos?.Contains(ideo) == true)
                .Select(faction =>
                {
                    string role = faction.ideos?.PrimaryIdeo == ideo ? "primary" : "secondary";
                    return faction.GetUniqueLoadID()
                        + ":"
                        + (faction.def?.defName ?? "<no-def>")
                        + ":"
                        + (faction.Name ?? "<no-name>")
                        + ":"
                        + role;
                })
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            int pawnCount = pawnReferences.TryGetValue(ideo, out int count) ? count : 0;
            bool orphan = factionReferences.Count == 0 && pawnCount == 0;

            builder.Append("local id=Ideo_").Append(ideo.id)
                .Append(" name=").Append(Display(ideo.name))
                .Append(" culture=").Append(Display(ideo.culture?.defName))
                .Append(" initial=").Append(ideo.initialPlayerIdeo)
                .Append(" hidden=").Append(ideo.hidden)
                .Append(" keys=").Append(globalKeys.Count == 0 ? "<local-generated>" : string.Join(",", globalKeys))
                .Append(" factions=").Append(factionReferences.Count == 0 ? "<none>" : string.Join(",", factionReferences))
                .Append(" pawns=").Append(pawnCount)
                .Append(" orphan=").Append(orphan)
                .AppendLine();
        }

        foreach (IGrouping<string, Ideo> duplicate in localIdeos
            .Where(ideo => ideo is not null && !string.IsNullOrWhiteSpace(ideo.name))
            .GroupBy(ideo => ideo.name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            builder.Append("duplicate-name name=").Append(Display(duplicate.Key))
                .Append(" localIds=")
                .Append(string.Join(",", duplicate.OrderBy(ideo => ideo.id).Select(ideo => "Ideo_" + ideo.id)))
                .AppendLine();
        }

        Log.Message(builder.ToString().TrimEnd());
    }

    private static string Display(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value!;
    }

    private static string ShortHash(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "<none>"
            : value!.Substring(0, Math.Min(12, value.Length));
    }
}
