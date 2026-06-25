using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

internal static class MercenarySkillUtility
{
    private const int PreferredBackstorySearchLimit = 6;

    private static readonly MercenaryProfession[] Professions =
    {
        new("Shooting", "Shooting", "ClashOfRim.Mercenary.ProfessionGunner", "ClashOfRim_MercenaryGunner", WorkTags.Violent, new[] { "Hunting" }),
        new("Melee", "Melee", "ClashOfRim.Mercenary.ProfessionWarrior", "ClashOfRim_MercenaryWarrior", WorkTags.Violent, Array.Empty<string>()),
        new("Medicine", "Medicine", "ClashOfRim.Mercenary.ProfessionDoctor", "ClashOfRim_MercenaryDoctor", WorkTags.None, new[] { "Doctor" }),
        new("Cooking", "Cooking", "ClashOfRim.Mercenary.ProfessionCook", "ClashOfRim_MercenaryCook", WorkTags.None, new[] { "Cooking" }),
        new("Construction", "Construction", "ClashOfRim.Mercenary.ProfessionBuilder", "ClashOfRim_MercenaryBuilder", WorkTags.None, new[] { "Construction" }),
        new("Mining", "Mining", "ClashOfRim.Mercenary.ProfessionMiner", "ClashOfRim_MercenaryMiner", WorkTags.None, new[] { "Mining" }),
        new("Plants", "Plants", "ClashOfRim.Mercenary.ProfessionFarmer", "ClashOfRim_MercenaryFarmer", WorkTags.None, new[] { "Growing", "PlantCutting" }),
        new("Animals", "Animals", "ClashOfRim.Mercenary.ProfessionHandler", "ClashOfRim_MercenaryHandler", WorkTags.None, new[] { "Handling" }),
        new("Crafting", "Crafting", "ClashOfRim.Mercenary.ProfessionCrafter", "ClashOfRim_MercenaryCrafter", WorkTags.None, new[] { "Crafting", "Smithing", "Tailoring" }),
        new("Artistic", "Artistic", "ClashOfRim.Mercenary.ProfessionArtist", "ClashOfRim_MercenaryArtist", WorkTags.None, new[] { "Art" }),
        new("Social", "Social", "ClashOfRim.Mercenary.ProfessionNegotiator", "ClashOfRim_MercenaryNegotiator", WorkTags.None, new[] { "Warden" }),
        new("Intellectual", "Intellectual", "ClashOfRim.Mercenary.ProfessionResearcher", "ClashOfRim_MercenaryResearcher", WorkTags.None, new[] { "Research" }),
        new("Hauling", null, "ClashOfRim.Mercenary.ProfessionPorter", "ClashOfRim_MercenaryPorter", WorkTags.None, new[] { "Hauling", "Cleaning" })
    };
    private static IReadOnlyList<MercenaryProfession>? cachedAvailableProfessions;

    public static IReadOnlyList<MercenaryProfession> AvailableProfessions()
    {
        return cachedAvailableProfessions ??= Professions
            .Where(profession => profession.PrimarySkillDefName is null
                || DefDatabase<SkillDef>.GetNamedSilentFail(profession.PrimarySkillDefName) is not null)
            .ToList();
    }

    public static string TierLabel(int level)
    {
        return level switch
        {
            7 => ClashOfRimText.Key("ClashOfRim.Mercenary.TierApprentice"),
            14 => ClashOfRimText.Key("ClashOfRim.Mercenary.TierSkilled"),
            20 => ClashOfRimText.Key("ClashOfRim.Mercenary.TierMaster"),
            _ => level.ToString()
        };
    }

    public static Pawn GenerateMercenaryPawn(string skillDefName, int skillLevel, string contractId)
    {
        MercenaryProfession profession = ResolveProfession(skillDefName);
        SkillDef? skill = string.IsNullOrWhiteSpace(profession.PrimarySkillDefName)
            ? null
            : DefDatabase<SkillDef>.GetNamedSilentFail(profession.PrimarySkillDefName);
        Pawn pawn = GeneratePawnWithRelevantBackstory(profession, skill);
        pawn.Name = PawnBioAndNameGenerator.GeneratePawnName(pawn, NameStyle.Full, null, false, null);
        ApplySkillProfile(pawn, skill, skillLevel);
        ApplyProfessionTraits(pawn, profession, skillLevel);
        EnableProfessionWork(pawn, profession);
        QuestUtility.AddQuestTag(ref pawn.questTags, QuestTag(contractId));
        return pawn;
    }

    public static void ApplySkillProfile(Pawn pawn, SkillDef? selectedSkill, int level)
    {
        if (pawn.skills is null)
        {
            return;
        }

        foreach (SkillRecord record in pawn.skills.skills)
        {
            record.levelInt = 0;
            record.passion = Passion.None;
            record.xpSinceLastLevel = 0f;
            record.xpSinceMidnight = 0f;
        }

        if (selectedSkill is null)
        {
            return;
        }

        SkillRecord selected = pawn.skills.GetSkill(selectedSkill);
        selected.levelInt = Math.Max(0, Math.Min(20, level));
        selected.passion = Passion.Major;
    }

    public static string ProfessionLabel(string skillDefName)
    {
        return ResolveProfession(skillDefName).Label;
    }

    public static MercenaryProfession ResolveProfession(string? key)
    {
        return Professions.FirstOrDefault(profession => string.Equals(profession.Key, key, StringComparison.Ordinal))
            ?? Professions[0];
    }

    public static string QuestTag(string contractId)
    {
        return "ClashOfRimMercenary_" + contractId;
    }

    private static void ApplyProfessionTraits(Pawn pawn, MercenaryProfession profession, int skillLevel)
    {
        if (pawn.story?.traits is null)
        {
            return;
        }

        AddTraitIfMissing(pawn, profession.TraitDefName);
        string? extraTraitDefName = ExtraTraitDefNameForLevel(profession, skillLevel);
        if (!string.IsNullOrWhiteSpace(extraTraitDefName))
        {
            AddTraitIfMissing(pawn, extraTraitDefName!);
        }

        pawn.Notify_DisabledWorkTypesChanged();
    }

    private static void EnableProfessionWork(Pawn pawn, MercenaryProfession profession)
    {
        if (pawn.workSettings is null)
        {
            return;
        }

        pawn.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
        foreach (string workTypeDefName in profession.RequiredWorkTypeDefNames)
        {
            WorkTypeDef? workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(workTypeDefName);
            if (workType is null || pawn.WorkTypeIsDisabled(workType) || pawn.workSettings.GetPriority(workType) > 0)
            {
                continue;
            }

            pawn.workSettings.SetPriority(workType, 3);
        }
    }

    private static Pawn GeneratePawnWithRelevantBackstory(MercenaryProfession profession, SkillDef? selectedSkill)
    {
        Pawn? fallback = null;
        for (int i = 0; i < PreferredBackstorySearchLimit; i++)
        {
            Pawn pawn = PawnGenerator.GeneratePawn(CreateGenerationRequest(profession));
            ApplyPreferredBackstories(pawn, profession, selectedSkill);
            if (BackstoryMatchesProfession(pawn.story?.Childhood, profession, selectedSkill, required: false)
                && BackstoryMatchesProfession(pawn.story?.Adulthood, profession, selectedSkill, required: true))
            {
                DiscardUnusedGeneratedPawn(fallback);
                return pawn;
            }

            if (fallback is null && BackstoriesAllowProfession(pawn, profession))
            {
                fallback = pawn;
            }
            else
            {
                DiscardUnusedGeneratedPawn(pawn);
            }
        }

        if (fallback is not null)
        {
            return fallback;
        }

        Pawn finalPawn = PawnGenerator.GeneratePawn(CreateGenerationRequest(profession));
        ApplyPreferredBackstories(finalPawn, profession, selectedSkill);
        return finalPawn;
    }

    private static PawnGenerationRequest CreateGenerationRequest(MercenaryProfession profession)
    {
        return new PawnGenerationRequest(
            PawnKindDefOf.Colonist,
            Faction.OfPlayer,
            PawnGenerationContext.NonPlayer,
            forceGenerateNewPawn: true,
            allowPregnant: false,
            allowAddictions: false,
            mustBeCapableOfViolence: profession.RequiredWorkTags.HasFlag(WorkTags.Violent),
            developmentalStages: DevelopmentalStage.Adult,
            biologicalAgeRange: new FloatRange(18f, 40f),
            validatorPreGear: pawn => BackstoriesAllowProfession(pawn, profession));
    }

    private static void ApplyPreferredBackstories(Pawn pawn, MercenaryProfession profession, SkillDef? selectedSkill)
    {
        if (pawn.story is null)
        {
            return;
        }

        BackstoryDef? childhood = SelectBackstory(BackstorySlot.Childhood, profession, selectedSkill, requiredSkillGain: false);
        BackstoryDef? adulthood = SelectBackstory(BackstorySlot.Adulthood, profession, selectedSkill, requiredSkillGain: true);
        if (childhood is not null)
        {
            pawn.story.Childhood = childhood;
        }

        if (adulthood is not null)
        {
            pawn.story.Adulthood = adulthood;
        }

        pawn.Notify_DisabledWorkTypesChanged();
    }

    private static BackstoryDef? SelectBackstory(
        BackstorySlot slot,
        MercenaryProfession profession,
        SkillDef? selectedSkill,
        bool requiredSkillGain)
    {
        IEnumerable<BackstoryDef> candidates = DefDatabase<BackstoryDef>.AllDefsListForReading
            .Where(backstory => backstory.shuffleable
                && backstory.slot == slot
                && BackstoryAllowsProfession(backstory, profession));
        if (selectedSkill is not null)
        {
            candidates = requiredSkillGain
                ? candidates.Where(backstory => BackstorySkillGain(backstory, selectedSkill) > 0)
                : candidates.Where(backstory => BackstorySkillGain(backstory, selectedSkill) >= 0);
        }

        if (!candidates.TryRandomElementByWeight(backstory => BackstoryWeight(backstory, selectedSkill, slot), out BackstoryDef backstoryDef))
        {
            return null;
        }

        return backstoryDef;
    }

    private static float BackstoryWeight(BackstoryDef backstory, SkillDef? selectedSkill, BackstorySlot slot)
    {
        float weight = 1f;
        if (selectedSkill is not null)
        {
            weight += Math.Max(0, BackstorySkillGain(backstory, selectedSkill)) * (slot == BackstorySlot.Adulthood ? 2f : 1f);
        }

        return weight;
    }

    private static bool BackstoryMatchesProfession(
        BackstoryDef? backstory,
        MercenaryProfession profession,
        SkillDef? selectedSkill,
        bool required)
    {
        if (backstory is null || !BackstoryAllowsProfession(backstory, profession))
        {
            return false;
        }

        return !required || selectedSkill is null || BackstorySkillGain(backstory, selectedSkill) > 0;
    }

    private static bool BackstoriesAllowProfession(Pawn pawn, MercenaryProfession profession)
    {
        return pawn.story is null
            || (BackstoryAllowsProfession(pawn.story.Childhood, profession)
                && BackstoryAllowsProfession(pawn.story.Adulthood, profession));
    }

    private static bool BackstoryAllowsProfession(BackstoryDef? backstory, MercenaryProfession profession)
    {
        if (backstory is null)
        {
            return true;
        }

        if (profession.RequiredWorkTags != WorkTags.None
            && (backstory.workDisables & profession.RequiredWorkTags) != WorkTags.None)
        {
            return false;
        }

        foreach (string workTypeDefName in profession.RequiredWorkTypeDefNames)
        {
            WorkTypeDef? workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(workTypeDefName);
            if (workType is not null && (backstory.workDisables & workType.workTags) != WorkTags.None)
            {
                return false;
            }
        }

        return true;
    }

    private static int BackstorySkillGain(BackstoryDef backstory, SkillDef selectedSkill)
    {
        return backstory.skillGains
            .Where(skillGain => skillGain.skill == selectedSkill)
            .Sum(skillGain => skillGain.amount);
    }

    private static void DiscardUnusedGeneratedPawn(Pawn? pawn)
    {
        if (pawn is null || pawn.Destroyed)
        {
            return;
        }

        if (pawn.Spawned)
        {
            pawn.DeSpawn();
        }

        if (Find.WorldPawns is not null)
        {
            Find.WorldPawns.PassToWorld(pawn, RimWorld.Planet.PawnDiscardDecideMode.Discard);
        }
        else
        {
            pawn.Destroy(DestroyMode.Vanish);
        }
    }

    private static void AddTraitIfMissing(Pawn pawn, string traitDefName)
    {
        TraitDef? traitDef = DefDatabase<TraitDef>.GetNamedSilentFail(traitDefName);
        if (traitDef is not null && !pawn.story.traits.HasTrait(traitDef))
        {
            pawn.story.traits.GainTrait(new Trait(traitDef));
        }
    }

    private static string? ExtraTraitDefNameForLevel(MercenaryProfession profession, int skillLevel)
    {
        if (!string.Equals(profession.Key, "Hauling", StringComparison.Ordinal))
        {
            if (profession.RequiredWorkTags.HasFlag(WorkTags.Violent))
            {
                return skillLevel switch
                {
                    >= 20 => "ClashOfRim_MercenaryCombatMaster",
                    >= 14 => "ClashOfRim_MercenaryCombatSkilled",
                    _ => "ClashOfRim_MercenaryCombatApprentice"
                };
            }

            return skillLevel switch
            {
                >= 20 => "ClashOfRim_MercenaryWorkerMaster",
                >= 14 => "ClashOfRim_MercenaryWorkerSkilled",
                _ => "ClashOfRim_MercenaryWorkerApprentice"
            };
        }

        return skillLevel switch
        {
            >= 20 => "ClashOfRim_MercenaryPorterMaster",
            >= 14 => "ClashOfRim_MercenaryPorterSkilled",
            _ => "ClashOfRim_MercenaryPorterApprentice"
        };
    }

}

internal sealed class MercenaryProfession
{
    public MercenaryProfession(
        string key,
        string? primarySkillDefName,
        string labelKey,
        string traitDefName,
        WorkTags requiredWorkTags,
        IReadOnlyList<string> requiredWorkTypeDefNames,
        string? extraTraitDefName = null)
    {
        Key = key;
        PrimarySkillDefName = primarySkillDefName;
        LabelKey = labelKey;
        TraitDefName = traitDefName;
        RequiredWorkTags = requiredWorkTags;
        RequiredWorkTypeDefNames = requiredWorkTypeDefNames;
        ExtraTraitDefName = extraTraitDefName;
    }

    public string Key { get; }

    public string? PrimarySkillDefName { get; }

    public string LabelKey { get; }

    public string TraitDefName { get; }

    public WorkTags RequiredWorkTags { get; }

    public IReadOnlyList<string> RequiredWorkTypeDefNames { get; }

    public string? ExtraTraitDefName { get; }

    public string Label => ClashOfRimText.Key(LabelKey);
}
