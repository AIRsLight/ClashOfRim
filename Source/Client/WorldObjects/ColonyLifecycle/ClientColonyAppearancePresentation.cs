using System;
using AIRsLight.ClashOfRim.Modules.World;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal sealed class ClientColonyAppearancePresentation : ClientWorldWritePresentation, IClientColonyAppearancePresentation
{
    internal ClientColonyAppearancePresentation() : base(static _ => { }) { }
    public void Open(string colony, ColonyAppearanceSelection current, Action<ColonyAppearanceSelection> save)
        => Find.WindowStack.Add(new ColonyAppearanceDialogWindow(colony, current, save));
}
