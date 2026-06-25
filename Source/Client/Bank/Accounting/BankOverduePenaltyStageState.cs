using Verse;

namespace AIRsLight.ClashOfRim.Bank;

public sealed class BankOverduePenaltyStageState : IExposable
{
    public int TriggerPenaltyCount;
    public string Kind = string.Empty;
    public float Severity;

    public void ExposeData()
    {
        Scribe_Values.Look(ref TriggerPenaltyCount, "triggerPenaltyCount");
        Scribe_Values.Look(ref Kind, "kind", string.Empty);
        Scribe_Values.Look(ref Severity, "severity");
    }
}
