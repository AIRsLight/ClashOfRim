namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

// Reconciliation may prepare snapshots and must run outside the mutation gate.
internal interface IRaidReconciliation
{
    void Reconcile(DateTimeOffset nowUtc);
}
