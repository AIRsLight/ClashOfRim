using Verse;

namespace AIRsLight.ClashOfRim.Achievements;

public sealed class Building_ClashAchievementTrophy : Building
{
    private AchievementTrophyData? trophyData;

    internal void SetTrophyData(AchievementTrophyData? data)
    {
        trophyData = data?.Clone();
        RefreshArtText();
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref trophyData, "clashOfRimAchievementTrophy");
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            RefreshArtText();
        }
    }

    public override void SpawnSetup(Map map, bool respawningAfterLoad)
    {
        base.SpawnSetup(map, respawningAfterLoad);
        RefreshArtText();
    }

    public override string GetInspectString()
    {
        string text = base.GetInspectString();
        if (trophyData is null)
        {
            return text;
        }

        string trophyLine = trophyData.BuildInspectLine();
        return string.IsNullOrWhiteSpace(text)
            ? trophyLine
            : text + "\n" + trophyLine;
    }

    private void RefreshArtText()
    {
        if (trophyData is null)
        {
            return;
        }

        AIRsLight.ClashOfRim.CoreCompatibility.CompArtFixedText.Set(
            this.TryGetComp<RimWorld.CompArt>(),
            trophyData.BuildArtDescription());
    }
}
