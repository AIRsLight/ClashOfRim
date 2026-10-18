using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Achievements;
using Verse;

namespace AIRsLight.ClashOfRim.Achievements;

// Retain the original save key and record type. Records exist only during Scribe's
// multi-phase loading/saving; the module is the sole runtime queue owner.
internal sealed class PendingAchievementCandidates : IExposable
{
    internal ClientAchievementCandidates Queue { get; } = new();
    private List<PendingAchievementCandidateRecord>? records;
    public void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
            records = Queue.Snapshot().Select(candidate => new PendingAchievementCandidateRecord(candidate)).ToList();
        Scribe_Collections.Look(ref records, "clashOfRimPendingAchievementCandidates", LookMode.Deep);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
            Queue.Restore(records?.Where(record => record is not null).Select(record => record.ToDto()));
        if (Scribe.mode is LoadSaveMode.PostLoadInit or LoadSaveMode.Saving) records = null;
    }
}
