using System;
using System.IO;
using System.Text;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;
using static AIRsLight.ClashOfRim.ClientWorldRuntimeValues;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientStorytellerRuntime : IClientStorytellerRuntime
{
    public IClientStorytellerSession Bind() => new Session(Current.Game);
    private sealed class Session : IClientStorytellerSession
    {
        private readonly Game? game;
        internal Session(Game? game) => this.game = game;
        public bool IsCurrent => ReferenceEquals(game, Current.Game);
        public bool IsPlaying => game is not null && Current.ProgramState == ProgramState.Playing;
        public ClientStorytellerBaseline Capture()
        {
            object? storyteller = ReadMember(game, "storyteller") ?? ReadMember(Find.GameInitData, "storyteller");
            string? storytellerDef = ReadDefName(ReadMember(storyteller, "def")) ?? ReadDefName(storyteller);
            object? difficulty = ReadMember(ReadMember(game, "storyteller"), "difficultyDef")
                ?? ReadMember(ReadMember(game, "storyteller"), "difficulty")
                ?? ReadMember(Find.GameInitData, "difficulty") ?? ReadMember(Find.GameInitData, "difficultyDef");
            string? difficultyDef = ReadDefName(difficulty);
            string? xml = CaptureValues(game?.storyteller?.difficulty, out string? failure);
            return new(storytellerDef, difficultyDef, xml, failure);
        }
        public IClientStorytellerChange? Prepare(ClientStorytellerBaseline baseline)
        {
            if (game is null) return null;
            var storyteller = DefDatabase<StorytellerDef>.GetNamedSilentFail(baseline.Storyteller);
            var difficultyDef = DefDatabase<DifficultyDef>.GetNamedSilentFail(baseline.Difficulty);
            if (storyteller is null || difficultyDef is null)
            { Log.Warning($"[ClashOfRim] Server storyteller/difficulty baseline could not be resolved: storyteller={baseline.Storyteller} difficulty={baseline.Difficulty}"); return null; }
            Difficulty? difficulty = string.IsNullOrWhiteSpace(baseline.ValuesXml) ? new Difficulty(difficultyDef) : ReadValues(baseline.ValuesXml!, difficultyDef);
            return difficulty is null ? null : new Change(this, new Storyteller(storyteller, difficultyDef, difficulty));
        }
        public void LockCommitment()
        {
            // GameInitData is released after map creation; runtime refresh also uses this path.
            if (IsCurrent && Find.GameInitData is { } init)
            { init.permadeathChosen = true; init.permadeath = false; }
        }
        private sealed record Change(Session Owner, Storyteller Storyteller) : IClientStorytellerChange
        {
            public void Apply()
            {
                if (!Owner.IsCurrent) return;
                Owner.game!.storyteller = Storyteller;
                Owner.LockCommitment();
            }
        }
    }
    private static string? CaptureValues(Difficulty? difficulty, out string? failure)
    {
        failure = null;
        if (difficulty is null) return null;
        if (Scribe.mode != LoadSaveMode.Inactive)
        { failure = "Cannot capture difficulty baseline while Scribe is active."; Log.Warning("[ClashOfRim] " + failure); return null; }
        try
        {
            string xml = Scribe.saver.DebugOutputFor(difficulty);
            if (string.IsNullOrWhiteSpace(xml)) failure = "Difficulty capture returned empty XML.";
            return xml;
        }
        catch (Exception ex) { failure = ex.GetType().Name + " " + ex.Message; Log.Warning("[ClashOfRim] Failed to capture difficulty baseline: " + failure); return null; }
    }
    private static Difficulty? ReadValues(string xml, DifficultyDef difficultyDef)
    {
        if (Scribe.mode != LoadSaveMode.Inactive)
        { Log.Warning("[ClashOfRim] Cannot apply difficulty baseline while Scribe is active."); return null; }
        string path = Path.Combine(Path.GetTempPath(), "ClashOfRimDifficulty-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path, "<root>\n" + xml + "\n</root>", Encoding.UTF8);
            Scribe.loader.InitLoading(path);
            Difficulty? difficulty = null;
            Scribe_Deep.Look(ref difficulty, "saveable", difficultyDef);
            Scribe.loader.FinalizeLoading();
            return difficulty;
        }
        catch (Exception ex)
        {
            Scribe.ForceStop();
            Log.Warning("[ClashOfRim] Failed to apply difficulty baseline: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
        finally
        {
            try { File.Delete(path); }
            catch { /* Temporary input cleanup must not replace the load outcome. */ }
        }
    }
}
