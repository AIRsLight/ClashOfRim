using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

// Used only by the transient preview document, never by a transferred/saved pawn.
// Reimplement the IExposable dispatch because the native ExposeData is nonvirtual.
internal sealed class PawnPreviewIdeoTracker : Pawn_IdeoTracker, IExposable
{
    public PawnPreviewIdeoTracker(Pawn pawn) : base(pawn) { }
    void IExposable.ExposeData()
    {
        if (Scribe.mode != LoadSaveMode.PostLoadInit) base.ExposeData();
    }
}
