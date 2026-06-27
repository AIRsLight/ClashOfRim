using System.Collections.Generic;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnExchangeLoadIdUtility
{
    public static void LocalizeRestoredPawn(Pawn pawn)
    {
        if (pawn is null || Find.UniqueIDsManager is null)
        {
            return;
        }

        LocalizeThing(pawn);
        LocalizeHeldThingIds(pawn);
        ClashOfRimCompatibilityApi.LocalizeRestoredPawnWithCompatibility(pawn);
        LocalizeHediffs(pawn);
        LocalizeAbilities(pawn);
    }

    public static void LocalizeRestoredThing(Thing? thing)
    {
        LocalizeThing(thing);
    }

    private static void LocalizeHeldThingIds(Pawn pawn)
    {
        List<ThingWithComps>? equipment = pawn.equipment?.AllEquipmentListForReading;
        if (equipment is not null)
        {
            for (int i = 0; i < equipment.Count; i++)
            {
                LocalizeThing(equipment[i]);
            }
        }

        List<Apparel>? apparel = pawn.apparel?.WornApparel;
        if (apparel is not null)
        {
            for (int i = 0; i < apparel.Count; i++)
            {
                LocalizeThing(apparel[i]);
            }
        }

        ThingOwner<Thing>? inventory = pawn.inventory?.innerContainer;
        if (inventory is not null)
        {
            foreach (Thing thing in inventory)
            {
                LocalizeThing(thing);
            }
        }

        Thing? carried = pawn.carryTracker?.CarriedThing;
        if (carried is not null)
        {
            LocalizeThing(carried);
        }
    }

    private static void LocalizeThing(Thing? thing)
    {
        if (thing?.def?.HasThingIDNumber != true)
        {
            return;
        }

        thing.thingIDNumber = -1;
        ThingIDMaker.GiveIDTo(thing);
    }

    private static void LocalizeHediffs(Pawn pawn)
    {
        List<Hediff>? hediffs = pawn.health?.hediffSet?.hediffs;
        if (hediffs is null)
        {
            return;
        }

        for (int i = 0; i < hediffs.Count; i++)
        {
            hediffs[i].loadID = Find.UniqueIDsManager.GetNextHediffID();
        }
    }

    private static void LocalizeAbilities(Pawn pawn)
    {
        if (pawn.abilities?.AllAbilitiesForReading is not { } abilities)
        {
            return;
        }

        var localized = new HashSet<Ability>();
        for (int i = 0; i < abilities.Count; i++)
        {
            LocalizeAbility(abilities[i], localized);
        }
    }

    private static void LocalizeAbility(Ability? ability, ISet<Ability> localized)
    {
        if (ability is null || !localized.Add(ability))
        {
            return;
        }

        int oldId = ability.Id;
        if (oldId >= 0)
        {
            string oldLoadId = "Ability_" + oldId;
            ability.Id = Find.UniqueIDsManager.GetNextAbilityID();
            string newLoadId = "Ability_" + ability.Id;
            RewriteAbilityVerbLoadIds(ability, oldLoadId, newLoadId);
        }
        else
        {
            ability.Id = Find.UniqueIDsManager.GetNextAbilityID();
        }
    }

    private static void RewriteAbilityVerbLoadIds(Ability ability, string oldLoadId, string newLoadId)
    {
        List<Verb>? verbs = ability.VerbTracker?.AllVerbs;
        if (verbs is null)
        {
            return;
        }

        for (int i = 0; i < verbs.Count; i++)
        {
            Verb? verb = verbs[i];
            if (verb is null || string.IsNullOrWhiteSpace(verb.loadID))
            {
                continue;
            }

            verb.loadID = verb.loadID.Replace(oldLoadId, newLoadId);
        }
    }
}
