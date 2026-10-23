using System;
using AIRsLight.ClashOfRim.Diplomacy;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

// A display preference for one admitted starting-site visit. Never changes the
// world's object registries, faction state, map parents or settlement validators.
internal sealed class StartingSiteColonyFilter
{
    private readonly Func<bool> current;
    private readonly World world;
    private bool closed;
    internal Window Page { get; }
    internal bool Enabled { get; private set; }
    internal StartingSiteColonyFilter(Window page, Func<bool> current, World world)
    { Page = page; this.current = current; this.world = world; }

    internal static bool Active => ClientWorldSetupPages.Current?.SiteFilter is { Enabled: true } filter
        && ReferenceEquals(filter.world, Find.World);
    internal static bool Hides(WorldObject value) => Active && IsNpcSettlement(value);

    private static bool IsNpcSettlement(WorldObject value)
        => value is Settlement && value.Faction?.IsPlayer != true
            && !PlayerFactionProxyUtility.IsServerPlayerProxy(value.Faction);

    internal void Refresh()
    { if (!closed && (!current() || !ReferenceEquals(world, Find.World))) Close(); }

    internal void Close()
    {
        closed = true;
        if (!Enabled) return;
        Enabled = false;
        InvalidateIcons();
    }

    internal void SetEnabled(bool value)
    {
        Refresh();
        if (closed || Enabled == value) return;
        Enabled = value;
        InvalidateIcons();
        if (!value) return;
        // Remove invisible selection brackets while retaining the selected tile.
        var selector = Find.WorldSelector;
        var tile = selector.SelectedTile;
        for (int i = selector.SelectedObjects.Count - 1; i >= 0; i--)
        {
            var selected = selector.SelectedObjects[i];
            if (!IsNpcSettlement(selected)) continue;
            if (!tile.Valid) tile = selected.Tile;
            selector.Deselect(selected);
        }
        if (tile.Valid) selector.SelectedTile = tile;
    }

    private void InvalidateIcons()
    {
        // WorldRenderer's notifier resolves Find.WorldGrid, which may already be
        // another world when this page expires. Restore this visit's own meshes.
        foreach (var layer in world.grid.PlanetLayers.Values)
            layer.SetDirty<WorldDrawLayer_WorldObjects>();
    }

    internal void Draw()
    {
        Refresh();
        if (closed) return;
        var font = Text.Font;
        var anchor = Text.Anchor;
        bool wordWrap = Text.WordWrap;
        try
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = true;
            string label = ClashOfRimText.Key("ClashOfRim.StartingSite.OnlyPlayerColonies");
            float width = Mathf.Min(440f, UI.screenWidth - 24f);
            float height = Mathf.Max(36f, Text.CalcHeight(label, width - 48f) + 16f);
            Rect rect = new((UI.screenWidth - width) / 2f, 55f, width, height);
            Widgets.DrawWindowBackground(rect);
            bool enabled = Enabled;
            Widgets.CheckboxLabeled(rect.ContractedBy(8f), label, ref enabled);
            SetEnabled(enabled);
            TooltipHandler.TipRegion(rect, ClashOfRimText.Key("ClashOfRim.StartingSite.OnlyPlayerColoniesTip"));
            GenUI.AbsorbClicksInRect(rect);
        }
        finally { Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wordWrap; }
    }
}
