using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Quests;

[DefOf]
public static class ClashOfRimQuestDefOf
{
    public static QuestScriptDef ClashOfRim_BankLoan = null!;
    public static QuestScriptDef ClashOfRim_BankDebt = null!;
    public static QuestScriptDef ClashOfRim_SupportPawn = null!;
    public static QuestScriptDef ClashOfRim_Mercenary = null!;

    static ClashOfRimQuestDefOf()
    {
        DefOfHelper.EnsureInitializedInCtor(typeof(ClashOfRimQuestDefOf));
    }
}
