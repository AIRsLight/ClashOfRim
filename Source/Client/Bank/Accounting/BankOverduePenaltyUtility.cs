using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Bank;

internal static class BankOverduePenaltyUtility
{
    public static void ApplyGlobalPenalty(
        int penaltyCount,
        IEnumerable<BankOverduePenaltyStageState>? stages,
        string unsupportedLogContext)
    {
        BankOverduePenaltyStageState? stage = SelectStage(penaltyCount, stages);
        string kind = string.IsNullOrWhiteSpace(stage?.Kind) ? "PsychicWhisper" : stage!.Kind ?? "PsychicWhisper";
        float severity = stage?.Severity > 0f ? stage.Severity : 1f;
        ApplyPenaltyStage(kind, severity, unsupportedLogContext);
    }

    private static BankOverduePenaltyStageState? SelectStage(
        int penaltyCount,
        IEnumerable<BankOverduePenaltyStageState>? stages)
    {
        List<BankOverduePenaltyStageState> ordered = stages?
            .Where(stage => stage.TriggerPenaltyCount > 0 && !string.IsNullOrWhiteSpace(stage.Kind))
            .OrderBy(stage => stage.TriggerPenaltyCount)
            .ToList()
            ?? new List<BankOverduePenaltyStageState>();
        if (ordered.Count == 0)
        {
            return null;
        }

        return ordered
            .Where(stage => stage.TriggerPenaltyCount <= penaltyCount)
            .LastOrDefault();
    }

    private static void ApplyPenaltyStage(string kind, float severity, string unsupportedLogContext)
    {
        if (string.Equals(kind, "PsychicWhisper", StringComparison.OrdinalIgnoreCase))
        {
            Find.LetterStack.ReceiveLetter(
                ClashOfRimText.Key("ClashOfRim.Bank.PsychicWhisperLabel"),
                ClashOfRimText.Key(
                    "ClashOfRim.Bank.PsychicWhisperText",
                    Math.Max(0f, severity).ToString("0.#").Named("SEVERITY")),
                LetterDefOf.NegativeEvent);
            return;
        }

        if (TryExecuteIncident(kind, unsupportedLogContext))
        {
            return;
        }

        Log.Warning("[ClashOfRim][Bank] Unsupported overdue penalty stage kind: "
            + kind
            + " context="
            + unsupportedLogContext);
        ApplyPenaltyStage("PsychicWhisper", 1f, unsupportedLogContext);
    }

    private static bool TryExecuteIncident(string incidentDefName, string unsupportedLogContext)
    {
        IncidentDef? incident = DefDatabase<IncidentDef>.GetNamedSilentFail(incidentDefName);
        Map? map = Find.CurrentMap ?? Find.Maps?.FirstOrDefault(candidate => candidate.IsPlayerHome);
        if (incident is null || map is null || !incident.TargetAllowed(map))
        {
            return false;
        }

        IncidentParms parms = StorytellerUtility.DefaultParmsNow(incident.category, map);
        parms.forced = true;
        if (incident.pointsScaleable)
        {
            StorytellerComp? comp = Find.Storyteller?.storytellerComps?
                .FirstOrDefault(candidate => candidate is StorytellerComp_OnOffCycle || candidate is StorytellerComp_RandomMain);
            if (comp is not null)
            {
                parms = comp.GenerateParms(incident.category, map);
                parms.forced = true;
            }
            else
            {
                parms.points = StorytellerUtility.DefaultThreatPointsNow(map);
            }
        }

        bool executed = incident.Worker.TryExecute(parms);
        if (!executed)
        {
            Log.Warning("[ClashOfRim][Bank] Overdue penalty incident could not execute: "
                + incidentDefName
                + " context="
                + unsupportedLogContext);
        }

        return executed;
    }
}
