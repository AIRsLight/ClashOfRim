using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

public sealed class GiftDeliveryDialogWindow : Window
{
    private const int InventoryCacheTicks = 60;

    private readonly ClashOfRimMod mod;
    private readonly Caravan caravan;
    private readonly ModWorldMapMarkerDto target;
    private readonly bool normalAvailable;
    private readonly bool forcedAvailable;
    private readonly List<TradeOfferSelection> selectedThings = new();
    private readonly Dictionary<string, string> countBuffers = new(StringComparer.Ordinal);
    private Vector2 inventoryScrollPosition;
    private Vector2 selectedScrollPosition;
    private bool forcedDelivery;
    private string message = string.Empty;
    private int nextInventoryRefreshTick;
    private IReadOnlyList<Thing> cachedInventoryThings = new List<Thing>();

    public GiftDeliveryDialogWindow(
        ClashOfRimMod mod,
        Caravan caravan,
        ModWorldMapMarkerDto target,
        bool normalAvailable,
        bool forcedAvailable,
        bool startForcedDelivery)
    {
        this.mod = mod;
        this.caravan = caravan;
        this.target = target;
        this.normalAvailable = normalAvailable;
        this.forcedAvailable = forcedAvailable;
        forcedDelivery = forcedAvailable && (startForcedDelivery || !normalAvailable);
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        forcePause = false;
        draggable = true;
    }

    public override Vector2 InitialSize => new(820f, 620f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.GiftDelivery.Title"));
        Text.Font = GameFont.Small;

        Widgets.Label(new Rect(inRect.x, inRect.y + 36f, inRect.width, 24f), ClashOfRimText.Key(
            "ClashOfRim.GiftDelivery.TargetLine",
            mod.FormatWorldMapTargetLabel(target).Named("TARGET")));

        Rect modeRect = new(inRect.x, inRect.y + 66f, inRect.width, 30f);
        DrawModeButtons(modeRect);

        Rect inventoryRect = new(inRect.x, inRect.y + 106f, (inRect.width - 12f) * 0.5f, 300f);
        Rect selectedRect = new(inventoryRect.xMax + 12f, inventoryRect.y, inventoryRect.width, inventoryRect.height);
        DrawInventoryPanel(inventoryRect);
        DrawSelectedPanel(selectedRect);

        Widgets.Label(new Rect(inRect.x, inventoryRect.yMax + 12f, inRect.width, 24f), ClashOfRimText.Key("ClashOfRim.GiftDelivery.MessageLabel"));
        message = Widgets.TextArea(new Rect(inRect.x, inventoryRect.yMax + 38f, inRect.width, 58f), message ?? string.Empty);

        Rect submitRect = new(inRect.xMax - 150f, inRect.yMax - 36f, 150f, 32f);
        Rect cancelRect = new(submitRect.x - 104f, submitRect.y, 96f, 32f);
        if (Widgets.ButtonText(cancelRect, ClashOfRimText.Key("ClashOfRim.Cancel")))
        {
            Close();
        }

        bool canSubmit = selectedThings.Count > 0;
        if (!canSubmit)
        {
            GUI.color = Color.gray;
        }

        if (Widgets.ButtonText(submitRect, SubmitLabel()) && canSubmit)
        {
            Submit();
        }

        GUI.color = Color.white;
    }

    public override void OnAcceptKeyPressed()
    {
        if (selectedThings.Count > 0)
        {
            Submit();
        }

        Event.current?.Use();
    }

    private void Submit()
    {
        if (selectedThings.Count == 0)
        {
            return;
        }

        IReadOnlyList<TradeOfferSelection> selections = selectedThings
            .Select(selection => new TradeOfferSelection(selection.Thing, selection.Count))
            .ToList();
        string formattedThings;
        try
        {
            formattedThings = FormatSelectedThings();
        }
        catch (ThingTransferRejectedException ex)
        {
            Messages.Message(
                ThingTransferPipeline.RejectionMessage(ex.RejectionCode),
                MessageTypeDefOf.RejectInput,
                historical: false);
            return;
        }

        string summary = ClashOfRimText.Key(
            forcedDelivery ? "ClashOfRim.GiftDelivery.ConfirmForced" : "ClashOfRim.GiftDelivery.ConfirmNormal",
            mod.FormatWorldMapTargetLabel(target).Named("TARGET"),
            formattedThings.Named("THINGS"));
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(summary, () =>
        {
            Close();
            mod.StartCreateGiftFromCaravan(caravan, target, selections, forcedDelivery, message);
        }));
    }

    private void DrawModeButtons(Rect rect)
    {
        const float width = 148f;
        Rect normalRect = new(rect.x, rect.y, width, rect.height);
        if (!forcedDelivery)
        {
            Widgets.DrawHighlightSelected(normalRect);
        }

        if (!normalAvailable)
        {
            GUI.color = Color.gray;
            Widgets.Label(normalRect, ClashOfRimText.Key("ClashOfRim.GiftDelivery.NormalUnavailable"));
            GUI.color = Color.white;
        }
        else if (Widgets.ButtonText(normalRect, ClashOfRimText.Key("ClashOfRim.GiftDelivery.NormalMode")))
        {
            forcedDelivery = false;
        }

        Rect forcedRect = new(rect.x + width + 8f, rect.y, width, rect.height);
        if (forcedDelivery)
        {
            Widgets.DrawHighlightSelected(forcedRect);
        }

        if (!forcedAvailable)
        {
            GUI.color = Color.gray;
            Widgets.Label(forcedRect, ClashOfRimText.Key("ClashOfRim.GiftDelivery.ForcedUnavailable"));
            GUI.color = Color.white;
            forcedDelivery = false;
            return;
        }

        if (Widgets.ButtonText(forcedRect, ClashOfRimText.Key("ClashOfRim.GiftDelivery.ForcedMode")))
        {
            forcedDelivery = true;
        }
    }

    private void DrawInventoryPanel(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.GiftDelivery.CaravanItems"));
        IReadOnlyList<Thing> things = GetInventoryThings();
        HashSet<Thing> selectedThingSet = selectedThings.Select(selection => selection.Thing).ToHashSet();

        Rect outRect = new(inner.x, inner.y + 28f, inner.width, inner.height - 28f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, things.Count * TradeUiUtility.RowHeight));
        Widgets.BeginScrollView(outRect, ref inventoryScrollPosition, viewRect);
        for (int index = 0; index < things.Count; index++)
        {
            DrawInventoryRow(
                new Rect(0f, index * TradeUiUtility.RowHeight, viewRect.width, TradeUiUtility.RowHeight),
                things[index],
                selectedThingSet.Contains(things[index]));
        }

        Widgets.EndScrollView();
    }

    private IReadOnlyList<Thing> GetInventoryThings()
    {
        int ticks = Find.TickManager?.TicksGame ?? 0;
        if (ticks < nextInventoryRefreshTick)
        {
            return cachedInventoryThings;
        }

        cachedInventoryThings = CaravanInventoryUtility.AllInventoryItems(caravan)
            .Where(thing => thing != null
                && !thing.Destroyed
                && thing.def?.category == ThingCategory.Item
                && TradeThingReferenceUtility.IsTradeableItem(thing))
            .OrderBy(thing => thing.def.label)
            .ThenBy(thing => thing.ThingID)
            .ToList();
        nextInventoryRefreshTick = ticks + InventoryCacheTicks;
        return cachedInventoryThings;
    }

    private void DrawInventoryRow(Rect row, Thing thing, bool selected)
    {
        if (selected)
        {
            Widgets.DrawHighlightSelected(row);
        }
        else
        {
            Widgets.DrawHighlightIfMouseover(row);
        }

        Rect iconRect = new(row.x + 3f, row.y + 3f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        TradeUiUtility.DrawThingIcon(iconRect, thing);
        Text.Anchor = TextAnchor.MiddleLeft;
        Text.WordWrap = false;
        Widgets.Label(new Rect(iconRect.xMax + 8f, row.y, row.width - iconRect.width - 84f, row.height), TradeUiUtility.ThingLabel(thing));
        Text.WordWrap = true;
        Text.Anchor = TextAnchor.UpperLeft;

        if (Widgets.ButtonText(new Rect(row.xMax - 58f, row.y + 4f, 54f, 26f), selected ? ClashOfRimText.Key("ClashOfRim.Added") : ClashOfRimText.Key("ClashOfRim.Add")))
        {
            AddThing(thing);
        }
    }

    private void DrawSelectedPanel(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.GiftDelivery.SelectedItems"));

        Rect outRect = new(inner.x, inner.y + 28f, inner.width, inner.height - 28f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, selectedThings.Count * 40f));
        Widgets.BeginScrollView(outRect, ref selectedScrollPosition, viewRect);
        for (int index = 0; index < selectedThings.Count; index++)
        {
            DrawSelectedRow(new Rect(0f, index * 40f, viewRect.width, 38f), selectedThings[index]);
        }

        Widgets.EndScrollView();
    }

    private void DrawSelectedRow(Rect row, TradeOfferSelection selection)
    {
        Thing thing = selection.Thing;
        Widgets.DrawHighlightIfMouseover(row);
        Rect iconRect = new(row.x + 3f, row.y + 5f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        TradeUiUtility.DrawThingIcon(iconRect, thing);
        Text.Anchor = TextAnchor.MiddleLeft;
        Text.WordWrap = false;
        Widgets.Label(new Rect(iconRect.xMax + 8f, row.y, row.width - 220f, row.height), TradeUiUtility.ThingLabel(thing));
        Text.WordWrap = true;
        Text.Anchor = TextAnchor.UpperLeft;

        string key = thing.ThingID;
        string nextText = Widgets.TextField(new Rect(row.xMax - 156f, row.y + 5f, 94f, 28f), countBuffers.TryGetValue(key, out string? buffered) ? buffered : selection.Count.ToString());
        countBuffers[key] = nextText;
        if (int.TryParse(nextText, out int parsed))
        {
            selection.Count = Math.Min(Math.Max(1, parsed), Math.Max(1, thing.stackCount));
        }

        if (Widgets.ButtonText(new Rect(row.xMax - 54f, row.y + 5f, 50f, 28f), ClashOfRimText.Key("ClashOfRim.Remove")))
        {
            selectedThings.Remove(selection);
            countBuffers.Remove(key);
        }
    }

    private void AddThing(Thing thing)
    {
        if (selectedThings.Any(selection => ReferenceEquals(selection.Thing, thing)))
        {
            return;
        }

        int count = Math.Max(1, thing.stackCount);
        selectedThings.Add(new TradeOfferSelection(thing, count));
        countBuffers[thing.ThingID] = count.ToString();
    }

    private string SubmitLabel()
    {
        return forcedDelivery
            ? ClashOfRimText.Key("ClashOfRim.GiftDelivery.SubmitForced")
            : ClashOfRimText.Key("ClashOfRim.GiftDelivery.SubmitNormal");
    }

    private string FormatSelectedThings()
    {
        IReadOnlyList<ModThingReferenceDto> references = selectedThings
            .Select(selection => TradeCaravanFulfillmentUtility.BuildThingReference(
                selection.Thing,
                caravan,
                mod.UserId,
                mod.ColonyId,
                mod.CurrentSnapshotId,
                selection.Count,
                forcedDelivery ? ThingReferenceSurfaces.ForcedDelivery : ThingReferenceSurfaces.Gift))
            .ToList();
        return TradeUiUtility.FormatThingList(references, asRequirement: false);
    }
}
