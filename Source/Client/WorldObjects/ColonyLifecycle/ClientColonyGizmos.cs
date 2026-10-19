using System;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

// Harmony creates these commands outside composition. Only the existing colony
// modules are exposed here; their captured scopes own the delayed interaction.
internal sealed class ClientColonyGizmos
{
    internal static ClientColonyGizmos? Current { get; private set; }
    internal static void Attach(ClientColonyGizmos? value) => Current = value;
    private readonly ClientColonyAppearance appearance;
    private readonly ClientColonyAbandonment abandonment;
    internal ClientColonyGizmos(ClientColonyAppearance appearance, ClientColonyAbandonment abandonment)
    { this.appearance = appearance; this.abandonment = abandonment; }

    internal Command_Action? Appearance(MapParent colony)
    {
        var choice = appearance.Command(new ClientColonyAppearanceTarget(colony));
        if (choice is null) return null;
        var selected = choice.Appearance;
        return new()
        {
            defaultLabel = ClashOfRimUiUtility.SelectionLabel(ClashOfRimText.Key("ClashOfRim.ColonyAppearance.CommandLabel")),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.ColonyAppearance.CommandDesc"),
            icon = RemoteColonyWorldIconCache.GetTexture(selected.Mode, selected.IconDefName, selected.ColorDefName, selected.ColorHex, relationKind: null),
            Order = 980f,
            action = Bound(choice.Open)
        };
    }
    internal Command_Action? Abandon(MapParent colony, Command? original)
    {
        var open = abandonment.Command(new ClientColonyAbandonTarget(colony));
        return open is null ? null : new()
        {
            defaultLabel = original?.Label ?? "CommandAbandonHome".Translate().ToString(),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.AbandonColony.CommandDesc"),
            icon = original?.icon,
            Order = original?.Order ?? GizmoOrder.AbandonSettlement,
            action = Bound(open)
        };
    }
    private Action Bound(Action open) => () => { if (ReferenceEquals(Current, this)) open(); };
}
