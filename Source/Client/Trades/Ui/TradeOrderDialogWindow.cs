using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

public sealed class TradeOrderDialogWindow : Window
{
    private const float DefaultListingFeeRate = 0.05f;
    private const float RequestedRowHeight = 98f;
    private const float RequestedRowContentHeight = 94f;
    private const int OfferCandidatesCacheTicks = 60;
    private static List<ThingDef>? cachedRequestableThingDefs;
    private static readonly IReadOnlyDictionary<string, int> DefaultFixedFeePerThing =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Wastepack"] = 100
        };

    private readonly ClashOfRimMod mod;
    private readonly Map map;
    private readonly List<TradeOfferSelection> offeredThings = new();
    private readonly Dictionary<Thing, TradeOfferSelection> offeredThingIndex = new(new ThingReferenceComparer());
    private readonly List<ModThingReferenceDto> requestedThings = new();
    private Vector2 offerScrollPosition;
    private Vector2 searchScrollPosition;
    private Vector2 selectedOfferScrollPosition;
    private Vector2 requestedScrollPosition;
    private readonly Dictionary<string, string> offeredCountBuffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> requestedCountBuffers = new(StringComparer.Ordinal);
    private string offerSearchText = string.Empty;
    private string searchText = string.Empty;
    private string? cachedSearchText;
    private List<ThingDef> cachedSearchResults = new();
    private string? cachedOfferSearchText;
    private bool cachedOfferRequireTradeBeaconRange;
    private int cachedOfferSelectionVersion = -1;
    private int nextOfferCandidatesRefreshTick;
    private IReadOnlyList<Thing> cachedOfferCandidates = new List<Thing>();
    private int offerSelectionVersion;
    private bool requireTradeBeaconRange = true;
    private bool quoteInProgress;
    private string? quoteStatus;
    private int quoteRequestVersion;

    public TradeOrderDialogWindow(ClashOfRimMod mod, Map map)
    {
        this.mod = mod;
        this.map = map;
        doCloseX = true;
        closeOnAccept = false;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        forcePause = false;
        draggable = true;
    }

    public override Vector2 InitialSize => new(960f, 760f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Trade.CreateOrderTitle"));
        Text.Font = GameFont.Small;

        float top = inRect.y + 38f;
        Rect offerRect = new(inRect.x, top, (inRect.width - 12f) * 0.5f, 322f);
        Rect requestRect = new(offerRect.xMax + 12f, top, offerRect.width, 322f);
        Rect selectedRect = new(inRect.x, offerRect.yMax + 12f, inRect.width, 224f);
        Rect footerRect = new(inRect.x, selectedRect.yMax + 12f, inRect.width, 96f);

        DrawOfferPanel(offerRect);
        DrawRequestSearchPanel(requestRect);
        Rect selectedOfferRect = new(selectedRect.x, selectedRect.y, (selectedRect.width - 12f) * 0.5f, selectedRect.height);
        Rect selectedRequestRect = new(selectedOfferRect.xMax + 12f, selectedRect.y, selectedOfferRect.width, selectedRect.height);
        DrawSelectedOfferPanel(selectedOfferRect);
        DrawRequestedPanel(selectedRequestRect);
        DrawFooter(footerRect);
    }

    private void DrawOfferPanel(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.OfferLocalThings"));
        bool previousRequireTradeBeaconRange = requireTradeBeaconRange;
        Widgets.CheckboxLabeled(
            new Rect(inner.x, inner.y + 28f, inner.width, 24f),
            ClashOfRimText.Key("ClashOfRim.Trade.RequireBeaconRange"),
            ref requireTradeBeaconRange);
        if (previousRequireTradeBeaconRange != requireTradeBeaconRange)
        {
            offeredThings.Clear();
            offeredThingIndex.Clear();
            offeredCountBuffers.Clear();
            offerSelectionVersion++;
            ClearOfferCandidateCache();
        }

        offerSearchText = Widgets.TextField(new Rect(inner.x, inner.y + 56f, inner.width, 28f), offerSearchText ?? string.Empty);
        IReadOnlyList<Thing> candidates = GetOfferCandidates();
        Rect outRect = new(inner.x, inner.y + 90f, inner.width, inner.height - 90f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, candidates.Count * TradeUiUtility.RowHeight));
        Widgets.BeginScrollView(outRect, ref offerScrollPosition, viewRect);

        if (candidates.Count == 0)
        {
            Widgets.Label(
                new Rect(0f, 0f, viewRect.width, 44f),
                ClashOfRimText.Key(offeredThings.Count > 0
                    ? "ClashOfRim.Trade.NoMoreOfferableThings"
                    : "ClashOfRim.Trade.NoOfferableThings"));
        }
        else
        {
            for (int index = 0; index < candidates.Count; index++)
            {
                Thing thing = candidates[index];
                Rect row = new(0f, index * TradeUiUtility.RowHeight, viewRect.width, TradeUiUtility.RowHeight);
                DrawOfferRow(row, thing);
            }
        }

        Widgets.EndScrollView();
    }

    private void DrawOfferRow(Rect row, Thing thing)
    {
        TradeOfferSelection? selection = FindOfferSelection(thing);
        bool selected = selection is not null;
        if (selected)
        {
            Widgets.DrawHighlightSelected(row);
        }
        else
        {
            Widgets.DrawHighlightIfMouseover(row);
        }

        Rect iconRect = new(row.x + 3f, row.y + 3f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        TradeUiUtility.DrawThingIconWithInfo(iconRect, thing);
        float labelRight = row.xMax - 66f;
        if (thing is Pawn pawn)
        {
            float curX = labelRight;
            TradePawnUtility.DrawPawnExtraIcons(pawn, row, ref curX);
            labelRight = curX - 4f;
        }

        Rect labelRect = new(iconRect.xMax + 8f, row.y, labelRight - iconRect.xMax - 8f, row.height);
        TradeUiUtility.DrawTruncatedLabel(labelRect, TradeUiUtility.ThingLabel(thing));

        Rect addRect = new(row.xMax - 58f, row.y + 4f, 54f, 26f);
        if (Widgets.ButtonText(addRect, selected ? ClashOfRimText.Key("ClashOfRim.Added") : ClashOfRimText.Key("ClashOfRim.Add")))
        {
            AddOfferedThing(thing);
            return;
        }
    }

    private void DrawRequestSearchPanel(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.RequestedGlobalItems"));
        searchText = Widgets.TextField(new Rect(inner.x, inner.y + 28f, inner.width, 28f), searchText ?? string.Empty);

        List<ThingDef> defs = SearchRequestedThingDefsCached(searchText);
        Rect outRect = new(inner.x, inner.y + 62f, inner.width, inner.height - 62f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, defs.Count * TradeUiUtility.RowHeight));
        Widgets.BeginScrollView(outRect, ref searchScrollPosition, viewRect);

        for (int index = 0; index < defs.Count; index++)
        {
            ThingDef def = defs[index];
            Rect row = new(0f, index * TradeUiUtility.RowHeight, viewRect.width, TradeUiUtility.RowHeight);
            DrawRequestSearchRow(row, def);
        }

        Widgets.EndScrollView();
    }

    private void DrawRequestSearchRow(Rect row, ThingDef def)
    {
        Widgets.DrawHighlightIfMouseover(row);
        Rect iconRect = new(row.x + 3f, row.y + 3f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        TradeUiUtility.DrawThingIconWithInfo(iconRect, def.defName);
        Rect labelRect = new(iconRect.xMax + 8f, row.y, row.width - iconRect.width - 72f, row.height);
        TradeUiUtility.DrawTruncatedLabel(labelRect, def.label.CapitalizeFirst() + " (" + def.defName + ")");

        Rect addRect = new(row.xMax - 58f, row.y + 4f, 54f, 26f);
        if (Widgets.ButtonText(addRect, ClashOfRimText.Key("ClashOfRim.Add")))
        {
            AddRequestedThing(def);
        }
    }

    private List<ThingDef> SearchRequestedThingDefsCached(string text)
    {
        string query = (text ?? string.Empty).Trim();
        if (cachedSearchText is not null && string.Equals(cachedSearchText, query, StringComparison.Ordinal))
        {
            return cachedSearchResults;
        }

        cachedSearchText = query;
        cachedSearchResults = SearchRequestedThingDefs(query).Take(80).ToList();
        return cachedSearchResults;
    }

    private void DrawSelectedOfferPanel(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.SelectedOffers"));

        Rect outRect = new(inner.x, inner.y + 28f, inner.width, inner.height - 28f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, offeredThings.Count * 40f));
        Widgets.BeginScrollView(outRect, ref selectedOfferScrollPosition, viewRect);

        if (offeredThings.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 34f), ClashOfRimText.Key("ClashOfRim.Trade.NoSelectedOffers"));
        }
        else
        {
            for (int index = 0; index < offeredThings.Count; index++)
            {
                Rect row = new(0f, index * 40f, viewRect.width, 38f);
                DrawSelectedOfferRow(row, offeredThings[index]);
            }
        }

        Widgets.EndScrollView();
    }

    private void DrawSelectedOfferRow(Rect row, TradeOfferSelection selection)
    {
        Widgets.DrawHighlightIfMouseover(row);
        Thing thing = selection.Thing;
        Rect iconRect = new(row.x + 3f, row.y + 5f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        TradeUiUtility.DrawThingIconWithInfo(iconRect, thing);
        bool isPawn = thing is Pawn;
        Rect labelRect = new(iconRect.xMax + 8f, row.y, row.width - (isPawn ? 76f : 222f), row.height);
        TradeUiUtility.DrawTruncatedLabel(labelRect, TradeUiUtility.ThingLabel(thing));

        string key = thing.ThingID;
        if (!isPawn)
        {
            Rect countRect = new(row.xMax - 156f, row.y + 5f, 94f, 28f);
            string currentText = offeredCountBuffers.TryGetValue(key, out string buffered)
                ? buffered
                : selection.Count.ToString();
            string nextText = Widgets.TextField(countRect, currentText);
            offeredCountBuffers[key] = nextText;
            if (int.TryParse(nextText, out int parsed))
            {
                selection.Count = Math.Min(Math.Max(1, thing.stackCount), Math.Max(1, parsed));
            }
        }
        else
        {
            selection.Count = 1;
        }

        Rect removeRect = new(row.xMax - 54f, row.y + 5f, 50f, 28f);
        if (Widgets.ButtonText(removeRect, ClashOfRimText.Key("ClashOfRim.Remove")))
        {
            offeredThings.Remove(selection);
            offeredThingIndex.Remove(thing);
            offeredCountBuffers.Remove(key);
            offerSelectionVersion++;
            ClearOfferCandidateCache();
        }
    }

    private void DrawRequestedPanel(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.SelectedRequests"));

        Rect outRect = new(inner.x, inner.y + 28f, inner.width, inner.height - 28f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, requestedThings.Count * RequestedRowHeight));
        Widgets.BeginScrollView(outRect, ref requestedScrollPosition, viewRect);

        if (requestedThings.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 34f), ClashOfRimText.Key("ClashOfRim.Trade.NoSelectedRequests"));
        }
        else
        {
            for (int index = 0; index < requestedThings.Count; index++)
            {
                Rect row = new(0f, index * RequestedRowHeight, viewRect.width, RequestedRowContentHeight);
                DrawRequestedEditorRow(row, requestedThings[index]);
            }
        }

        Widgets.EndScrollView();
    }

    private void DrawRequestedEditorRow(Rect row, ModThingReferenceDto thing)
    {
        Widgets.DrawHighlightIfMouseover(row);
        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);

        Rect iconRect = new(row.x + 3f, row.y + 5f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        TradeUiUtility.DrawThingIconWithInfo(iconRect, thing);
        Rect labelRect = new(iconRect.xMax + 8f, row.y, row.width - iconRect.width - 74f, 32f);
        TradeUiUtility.DrawTruncatedLabel(labelRect, TradeUiUtility.ThingLabel(thing));

        string key = RequestedThingKey(thing);
        Rect removeRect = new(row.xMax - 54f, row.y + 5f, 50f, 28f);
        if (Widgets.ButtonText(removeRect, ClashOfRimText.Key("ClashOfRim.Remove")))
        {
            requestedThings.Remove(thing);
            requestedCountBuffers.Remove(key);
            return;
        }

        ClashOfRimCompatibilityApi.ClearThingReferenceMetadata(ThingReferenceSurfaces.TradeRequest, def, thing);

        float x = iconRect.xMax + 8f;
        float controlsY = row.y + 34f;
        Rect countRect = new(x, controlsY, 72f, 24f);
        string currentText = requestedCountBuffers.TryGetValue(key, out string buffered)
            ? buffered
            : thing.StackCount.ToString();
        string nextText = Widgets.TextField(countRect, currentText);
        requestedCountBuffers[key] = nextText;
        if (int.TryParse(nextText, out int parsed))
        {
            thing.StackCount = Math.Max(1, parsed);
            if (thing.StackCount <= 0)
            {
                requestedThings.Remove(thing);
            }
        }

        x += 82f;
        if (TradeThingReferenceUtility.DefSupportsStuff(def))
        {
            DrawStuffButton(new Rect(x, controlsY, 92f, 24f), def!, thing);
            x += 100f;
        }

        DrawQualityButton(new Rect(x, controlsY, 94f, 24f), def, thing);

        x += 102f;
        DrawHitPointsSlider(new Rect(x, controlsY - 2f, row.xMax - x - 4f, 28f), def, thing);

        Rect extensionRect = new(iconRect.xMax + 8f, row.y + 64f, row.xMax - iconRect.xMax - 12f, 28f);
        ClashOfRimCompatibilityApi.TryDrawThingReferenceEditor(
            ThingReferenceSurfaces.TradeRequest,
            def,
            thing,
            extensionRect,
            out _);
    }

    private static void DrawStuffButton(Rect rect, ThingDef def, ModThingReferenceDto thing)
    {
        string? selectedStuff = !string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName)
            ? thing.MinifiedInnerStuffDefName
            : thing.StuffDefName;
        string label = string.IsNullOrWhiteSpace(selectedStuff)
            ? ClashOfRimText.Key("ClashOfRim.Trade.StuffAny")
            : TradeThingReferenceUtility.StuffLabel(selectedStuff);
        if (!Widgets.ButtonText(rect, label))
        {
            return;
        }

        List<FloatMenuOption> options = new()
        {
            new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.Any"), () => SetRequestedStuff(thing, null))
        };
        foreach (ThingDef stuff in TradeThingReferenceUtility.AllowedStuffDefs(def))
        {
            ThingDef captured = stuff;
            options.Add(new FloatMenuOption(captured.LabelCap, () => SetRequestedStuff(thing, captured.defName)));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    private void DrawQualityButton(Rect rect, ThingDef? def, ModThingReferenceDto thing)
    {
        if (!TradeUiUtility.DefSupportsQuality(def))
        {
            GUI.color = Color.gray;
            Widgets.Label(rect, def?.category == ThingCategory.Pawn
                ? ClashOfRimText.Key("ClashOfRim.Trade.PawnRequirement")
                : ClashOfRimText.Key("ClashOfRim.Trade.NoQuality"));
            GUI.color = Color.white;
            return;
        }

        string label = string.IsNullOrWhiteSpace(thing.Quality)
            ? ClashOfRimText.Key("ClashOfRim.Trade.QualityAny")
            : ClashOfRimText.Key("ClashOfRim.Trade.QualitySelected", TradeUiUtility.FormatQualityLabel(thing.Quality).Named("QUALITY"));
        if (Widgets.ButtonText(rect, label))
        {
            List<FloatMenuOption> options = new()
            {
                new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.Any"), () => SetRequestedQuality(thing, null))
            };
            foreach (QualityCategory quality in Enum.GetValues(typeof(QualityCategory)).Cast<QualityCategory>())
            {
                QualityCategory captured = quality;
                options.Add(new FloatMenuOption(TradeUiUtility.FormatQualityLabel(captured), () => SetRequestedQuality(thing, captured.ToString())));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    private void DrawHitPointsSlider(Rect rect, ThingDef? def, ModThingReferenceDto thing)
    {
        if (!TradeUiUtility.DefSupportsHitPoints(def))
        {
            GUI.color = Color.gray;
            Widgets.Label(rect, def?.category == ThingCategory.Pawn
                ? ClashOfRimText.Key("ClashOfRim.Trade.NoPawnHitPoints")
                : ClashOfRimText.Key("ClashOfRim.Trade.NoHitPoints"));
            GUI.color = Color.white;
            return;
        }

        int percent = thing.HitPoints.HasValue
            ? TradeUiUtility.HitPointsPercent(thing.HitPoints.Value, def!)
            : 100;
        Widgets.Label(new Rect(rect.x, rect.y, 52f, rect.height), percent + "%");
        float sliderValue = percent;
        sliderValue = Widgets.HorizontalSlider(
            new Rect(rect.x + 54f, rect.y + 2f, rect.width - 54f, 28f),
            sliderValue,
            1f,
            100f,
            roundTo: 1f);
        SetRequestedHitPoints(thing, TradeUiUtility.HitPointsFromPercent(def!, Mathf.RoundToInt(sliderValue)));
    }

    private static void SetRequestedQuality(ModThingReferenceDto thing, string? quality)
    {
        thing.Quality = quality;
        if (!string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName))
        {
            thing.MinifiedInnerQuality = quality;
        }
    }

    private static void SetRequestedStuff(ModThingReferenceDto thing, string? stuffDefName)
    {
        if (!string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName))
        {
            thing.MinifiedInnerStuffDefName = stuffDefName;
        }
        else
        {
            thing.StuffDefName = stuffDefName;
        }
    }

    private static void SetRequestedHitPoints(ModThingReferenceDto thing, int hitPoints)
    {
        thing.HitPoints = hitPoints;
        if (!string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName))
        {
            thing.MinifiedInnerHitPoints = hitPoints;
        }
    }

    private void DrawFooter(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        int feeSilver = CalculateFeeSilver();
        int fixedFeeSilver = CalculateFixedFeeSilver();
        int availableSilver = CountAvailableFeeSilver();
        string feeText = fixedFeeSilver > 0
            ? ClashOfRimText.Key(
                "ClashOfRim.Trade.FeeWithFixed",
                feeSilver.Named("FEE"),
                fixedFeeSilver.Named("FIXED"),
                availableSilver.Named("AVAILABLE"))
            : ClashOfRimText.Key(
                "ClashOfRim.Trade.Fee",
                feeSilver.Named("FEE"),
                availableSilver.Named("AVAILABLE"));
        Widgets.Label(new Rect(inner.x, inner.y, 420f, 28f), feeText);

        Widgets.Label(new Rect(inner.x, inner.y + 26f, inner.width - 170f, 24f), BuildEstimatedValuesLine());
        Widgets.Label(new Rect(inner.x, inner.y + 58f, inner.width - 170f, 28f), quoteInProgress
            ? ClashOfRimText.Key("ClashOfRim.Trade.StatusQuotingFee")
            : quoteStatus ?? BuildSubmitHint());
        bool canSubmit = CanSubmit() && !quoteInProgress;
        Rect submitRect = new(inner.xMax - 156f, inner.y + 54f, 156f, 32f);
        if (Widgets.ButtonText(submitRect, quoteInProgress
                ? ClashOfRimText.Key("ClashOfRim.Trade.StatusQuotingFeeShort")
                : ClashOfRimText.Key("ClashOfRim.Trade.SubmitOrder")))
        {
            if (!canSubmit)
            {
                Messages.Message(BuildSubmitFailureHint(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            StartQuoteAndConfirm();
        }
    }

    private void StartQuoteAndConfirm()
    {
        List<TradeOfferSelection> offeredSnapshot = offeredThings
            .Select(selection => new TradeOfferSelection(selection.Thing, selection.Count))
            .ToList();
        List<ModThingReferenceDto> offeredReferences = offeredThings
            .Select(ToOfferReference)
            .ToList();
        List<ModThingReferenceDto> requestedReferences = requestedThings
            .Select(CloneThingReference)
            .ToList();
        int version = ++quoteRequestVersion;
        quoteInProgress = true;
        quoteStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusQuotingFee");

        Task.Run(async () =>
        {
            try
            {
                ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto> quote =
                    await mod.QuoteTradeOrderFeeAsync(offeredReferences, requestedReferences);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (version != quoteRequestVersion)
                    {
                        return;
                    }

                    quoteInProgress = false;
                    ModProtocolResponseDto? result = quote.Response?.Result;
                    if (!quote.Success || quote.Response is null || result?.Accepted != true)
                    {
                        quoteStatus = quote.Success && result is not null
                            ? ClashOfRimText.Key(
                                "ClashOfRim.Trade.StatusFeeQuoteRejected",
                                (result.Message ?? string.Empty).Named("MESSAGE"))
                            : ClashOfRimText.Key(
                                "ClashOfRim.Trade.StatusFeeQuoteFailed",
                                (quote.Message ?? quote.ErrorCode ?? string.Empty).Named("MESSAGE"));
                        Messages.Message(quoteStatus, MessageTypeDefOf.RejectInput, historical: false);
                        return;
                    }

                    int requiredFee = Math.Max(0, quote.Response.RequiredFeeSilver);
                    int availableSilver = CountAvailableFeeSilver();
                    if (availableSilver < requiredFee)
                    {
                        quoteStatus = ClashOfRimText.Key(
                            "ClashOfRim.Trade.StatusFeeSilverInsufficient",
                            requiredFee.Named("NEEDED"),
                            availableSilver.Named("AVAILABLE"));
                        Messages.Message(quoteStatus, MessageTypeDefOf.RejectInput, historical: false);
                        return;
                    }

                    quoteStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusFeeQuoted", requiredFee.Named("FEE"));
                    TradeOrderDraft draft = new(
                        offeredSnapshot,
                        offeredReferences,
                        requestedReferences,
                        requiredFee,
                        requireTradeBeaconRange,
                        allowSelfPickup: true,
                        allowServerDropPod: true);
                    ShowCreateConfirmation(draft);
                });
            }
            catch (Exception ex)
            {
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (version != quoteRequestVersion)
                    {
                        return;
                    }

                    quoteInProgress = false;
                    quoteStatus = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusFeeQuoteFailed",
                        ex.Message.Named("MESSAGE"));
                    Messages.Message(quoteStatus, MessageTypeDefOf.RejectInput, historical: false);
                });
            }
        });
    }

    private void ShowCreateConfirmation(TradeOrderDraft draft)
    {
        string summary = ClashOfRimText.Key("ClashOfRim.Trade.ConfirmCreateTitle")
            + "\n\n"
            + ClashOfRimText.Key(
                "ClashOfRim.Trade.ConfirmCreateExchange",
                TradeUiUtility.FormatThingList(draft.OfferedThings, asRequirement: false).Named("OFFERED"),
                TradeUiUtility.FormatThingList(draft.RequestedThings).Named("REQUESTED"))
            + "\n"
            + BuildEstimatedValuesLine()
            + "\n"
            + ClashOfRimText.Key("ClashOfRim.Trade.ConfirmCreateFee", draft.FeeSilver.Named("FEE"));
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(summary, () =>
        {
            Action create = () =>
            {
                Close();
                mod.StartCreateManualTradeOrder(draft);
            };
            if (draft.RequestedThings.Count == 0)
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    ClashOfRimText.Key("ClashOfRim.Trade.ConfirmNoRequestedThings"),
                    create));
                return;
            }

            create();
        }));
    }

    private bool CanSubmit()
    {
        return (offeredThings.Count > 0 || requestedThings.Count > 0)
            && requestedThings.All(HasCompleteRequest);
    }

    private string BuildSubmitHint()
    {
        if (CanSubmit())
        {
            return ClashOfRimText.Key(
                "ClashOfRim.Trade.SubmitHintReady",
                offeredThings.Count.Named("OFFERCOUNT"),
                requestedThings.Count.Named("REQUESTCOUNT"));
        }

        return BuildSubmitFailureHint();
    }

    private string BuildSubmitFailureHint()
    {
        if (offeredThings.Count == 0 && requestedThings.Count == 0)
        {
            return ClashOfRimText.Key("ClashOfRim.Trade.NoSelectedTradeThings");
        }

        if (!requestedThings.All(HasCompleteRequest))
        {
            return ClashOfRimText.Key("ClashOfRim.Trade.SubmitHintIncompleteRequest");
        }

        int neededSilver = CalculateFeeSilver();
        int availableSilver = CountAvailableFeeSilver();
        if (availableSilver < neededSilver)
        {
            return ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusFeeSilverInsufficient",
                neededSilver.Named("NEEDED"),
                availableSilver.Named("AVAILABLE"));
        }

        return ClashOfRimText.Key("ClashOfRim.Trade.SubmitHintMissing");
    }

    private string BuildEstimatedValuesLine()
    {
        string requestedValue = TryCalculateRequestedEstimatedValue(out float requestedMarketValue)
            ? TradeUiUtility.FormatMarketValue(requestedMarketValue)
            : ClashOfRimText.Key("ClashOfRim.Unknown");
        return ClashOfRimText.Key(
            "ClashOfRim.Trade.EstimatedValues",
            TradeUiUtility.FormatMarketValue(CalculateOfferedEstimatedValue()).Named("OFFERED"),
            requestedValue.Named("REQUESTED"));
    }

    private IReadOnlyList<Thing> GetOfferCandidates()
    {
        string query = (offerSearchText ?? string.Empty).Trim();
        int ticks = Find.TickManager?.TicksGame ?? 0;
        if (cachedOfferSearchText is not null
            && ticks < nextOfferCandidatesRefreshTick
            && string.Equals(cachedOfferSearchText, query, StringComparison.Ordinal)
            && cachedOfferRequireTradeBeaconRange == requireTradeBeaconRange
            && cachedOfferSelectionVersion == offerSelectionVersion)
        {
            return cachedOfferCandidates;
        }

        var candidates = new List<Thing>();
        foreach (Thing thing in TradeInventoryUtility.AccessibleMapThings(map, requireTradeBeaconRange))
        {
            if (!offeredThingIndex.ContainsKey(thing)
                && MatchesOfferSearch(thing, query))
            {
                candidates.Add(thing);
            }
        }

        candidates.Sort(CompareOfferCandidates);
        cachedOfferCandidates = candidates;
        cachedOfferSearchText = query;
        cachedOfferRequireTradeBeaconRange = requireTradeBeaconRange;
        cachedOfferSelectionVersion = offerSelectionVersion;
        nextOfferCandidatesRefreshTick = ticks + OfferCandidatesCacheTicks;
        return cachedOfferCandidates;
    }

    private static bool MatchesOfferSearch(Thing thing, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return thing.def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || thing.def.label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || thing.LabelShortCap.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int CompareOfferCandidates(Thing left, Thing right)
    {
        int labelCompare = string.Compare(
            left.def?.label,
            right.def?.label,
            StringComparison.Ordinal);
        if (labelCompare != 0)
        {
            return labelCompare;
        }

        return string.Compare(left.ThingID, right.ThingID, StringComparison.Ordinal);
    }

    private void AddOfferedThing(Thing thing)
    {
        TradeOfferSelection? selection = FindOfferSelection(thing);
        if (selection is null)
        {
            int count = thing is Pawn ? 1 : Math.Max(1, thing.stackCount);
            selection = new TradeOfferSelection(thing, count);
            offeredThings.Add(selection);
            offeredThingIndex[thing] = selection;
            offeredCountBuffers[thing.ThingID] = count.ToString();
            offerSelectionVersion++;
            ClearOfferCandidateCache();
        }
    }

    private TradeOfferSelection? FindOfferSelection(Thing thing)
    {
        return offeredThingIndex.TryGetValue(thing, out TradeOfferSelection selection)
            ? selection
            : null;
    }

    private void ClearOfferCandidateCache()
    {
        cachedOfferSearchText = null;
        cachedOfferCandidates = new List<Thing>();
        nextOfferCandidatesRefreshTick = 0;
    }

    private void AddRequestedThing(ThingDef def)
    {
        bool requiresUniqueRequest = ClashOfRimCompatibilityApi.IsThingReferenceUniqueRequest(def);
        string requestKey = RequestedThingKey(def);
        ModThingReferenceDto? existing = requiresUniqueRequest
            ? null
            : requestedThings.FirstOrDefault(thing => RequestedThingKey(thing) == requestKey);
        if (existing is null)
        {
            string globalKey = requiresUniqueRequest
                ? "market:any/thing:" + requestKey + "/request:" + Guid.NewGuid().ToString("N")
                : "market:any/thing:" + requestKey;
            ModThingReferenceDto reference = new()
            {
                GlobalKey = globalKey,
                DefName = IsPackableBuildingDef(def) ? def.minifiedDef?.defName : def.defName,
                StackCount = 1,
                HitPoints = TradeUiUtility.DefSupportsHitPoints(def)
                    ? TradeUiUtility.DefaultHitPoints(def)
                    : null
            };
            ClashOfRimCompatibilityApi.ApplyThingReferenceDefaultMetadata(ThingReferenceSurfaces.TradeRequest, def, reference);

            if (IsPackableBuildingDef(def))
            {
                reference.MinifiedInnerDefName = def.defName;
                reference.MinifiedInnerStuffDefName = null;
                reference.MinifiedInnerHitPoints = reference.HitPoints;
            }

            requestedThings.Add(reference);
            requestedCountBuffers[RequestedThingKey(reference)] = "1";
            return;
        }

        existing.StackCount += 1;
        requestedCountBuffers[requestKey] = existing.StackCount.ToString();
    }

    private static IEnumerable<ThingDef> SearchRequestedThingDefs(string query)
    {
        string normalized = (query ?? string.Empty).Trim();
        IEnumerable<ThingDef> defs = RequestableThingDefs();

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            defs = defs.Where(def =>
                def.defName.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0
                || def.label.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        return defs;
    }

    private static List<ThingDef> RequestableThingDefs()
    {
        return cachedRequestableThingDefs ??= DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => IsPackableBuildingDef(def)
                || TradeThingReferenceUtility.IsBookDef(def)
                || IsRequestableTradeThingDef(def))
            .Where(def => !def.IsCorpse)
            .OrderBy(def => def.label)
            .ThenBy(def => def.defName)
            .ToList();
    }

    private static bool IsRequestableTradeThingDef(ThingDef def)
    {
        if (def.category == ThingCategory.Item)
        {
            return def.PlayerAcquirable && TradeThingReferenceUtility.IsTradeableItemDef(def);
        }

        if (def.category != ThingCategory.Pawn)
        {
            return false;
        }

        if (def.race?.Animal == true)
        {
            return def.PlayerAcquirable;
        }

        return def.race?.IsMechanoid == true;
    }

    private static bool IsPackableBuildingDef(ThingDef def)
    {
        return def.category == ThingCategory.Building
            && def.Minifiable
            && def.minifiedDef is not null;
    }

    private static string RequestedThingKey(ThingDef def)
    {
        return IsPackableBuildingDef(def)
            ? "minified:" + def.defName
            : def.defName;
    }

    private static string RequestedThingKey(ModThingReferenceDto thing)
    {
        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        if (ClashOfRimCompatibilityApi.IsThingReferenceUniqueRequest(def))
        {
            return thing.GlobalKey;
        }

        return !string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName)
            ? "minified:" + thing.MinifiedInnerDefName
            : thing.DefName ?? thing.GlobalKey;
    }

    private static bool HasCompleteRequest(ModThingReferenceDto thing)
    {
        ThingDef? def = TradeThingReferenceUtility.ResolveReferenceDef(thing);
        return ClashOfRimCompatibilityApi.IsThingReferenceComplete(ThingReferenceSurfaces.TradeRequest, def, thing);
    }

    private ModThingReferenceDto ToOfferReference(TradeOfferSelection selection)
    {
        Thing thing = selection.Thing;
        if (thing is Pawn pawn)
        {
            string globalKey = PawnGlobalIdUtility.Build(mod.UserId, pawn);
            return TradePawnUtility.BuildPawnReference(
                pawn,
                globalKey,
                mod.UserId,
                mod.ColonyId,
                mod.CurrentSnapshotId,
                "trade-order:" + map.uniqueID);
        }

        CompBiocodable? biocodable = thing.TryGetComp<CompBiocodable>();
        return TradeThingReferenceUtility.BuildThingReference(
            thing,
            $"owner:{mod.UserId}/colony:{mod.ColonyId}/snapshot:{mod.CurrentSnapshotId}/map:Map_{map.uniqueID}/thing:{thing.ThingID}",
            selection.Count,
            BuildBiocodedPawnGlobalId(biocodable?.CodedPawn),
            ThingReferenceSurfaces.TradeOffer);
    }

    private int CalculateFeeSilver()
    {
        float totalValue = CalculateOfferedEstimatedValue();
        int baseFee = 0;
        if (totalValue > 0.01f)
        {
            baseFee = Math.Max(1, Mathf.CeilToInt(totalValue * ResolveListingFeeRate()));
        }

        return baseFee + CalculateFixedFeeSilver();
    }

    private float CalculateOfferedEstimatedValue()
    {
        return offeredThings.Sum(selection => EstimateFeeMarketValue(selection.Thing) * Math.Max(1, selection.Count));
    }

    private static float EstimateFeeMarketValue(Thing thing)
    {
        if (thing is Pawn)
        {
            try
            {
                return Mathf.Max(0f, thing.def.GetStatValueAbstract(StatDefOf.MarketValue));
            }
            catch (Exception)
            {
                return Mathf.Max(0f, thing.MarketValue);
            }
        }

        return Mathf.Max(0f, thing.MarketValue);
    }

    private bool TryCalculateRequestedEstimatedValue(out float marketValue)
    {
        marketValue = 0f;
        foreach (ModThingReferenceDto thing in requestedThings)
        {
            if (!TradeUiUtility.TryEstimateTotalMarketValue(thing, out float thingMarketValue))
            {
                marketValue = 0f;
                return false;
            }

            marketValue += thingMarketValue;
        }

        return true;
    }

    private int CalculateFixedFeeSilver()
    {
        return offeredThings.Sum(selection =>
        {
            IReadOnlyDictionary<string, int> fixedFees = ResolveFixedFeePerThing();
            if (!fixedFees.TryGetValue(selection.Thing.def.defName, out int perThingFee)
                || perThingFee <= 0)
            {
                return 0;
            }

            return perThingFee * Math.Max(1, selection.Count);
        });
    }

    private float ResolveListingFeeRate()
    {
        float? configured = mod.AdminStatusSnapshot?.Configuration?.TradeBaseFeeRate;
        return configured.HasValue && configured.Value >= 0f
            ? configured.Value
            : DefaultListingFeeRate;
    }

    private IReadOnlyDictionary<string, int> ResolveFixedFeePerThing()
    {
        List<ModAdminFixedTradeFeeDto>? configured = mod.AdminStatusSnapshot?.Configuration?.FixedTradeFees;
        if (configured is null || configured.Count == 0)
        {
            return DefaultFixedFeePerThing;
        }

        return configured
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ThingDefName) && entry.SilverPerUnit > 0)
            .GroupBy(entry => entry.ThingDefName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => Math.Max(0, group.Last().SilverPerUnit),
                StringComparer.OrdinalIgnoreCase);
    }

    private int CountAvailableFeeSilver()
    {
        int total = 0;
        foreach (Thing thing in FeeSilverCandidates())
        {
            if (thing.def == ThingDefOf.Silver)
            {
                total += AvailableCountAfterOfferSelection(thing);
            }
        }

        return total;
    }

    private IEnumerable<Thing> FeeSilverCandidates()
    {
        return TradeInventoryUtility.AccessibleMapItems(map, requireTradeBeaconRange);
    }

    private int AvailableCountAfterOfferSelection(Thing thing)
    {
        return offeredThingIndex.TryGetValue(thing, out TradeOfferSelection selection)
            ? Math.Max(0, thing.stackCount - selection.Count)
            : thing.stackCount;
    }

    private static ModThingReferenceDto CloneThingReference(ModThingReferenceDto thing)
    {
        return new ModThingReferenceDto
        {
            GlobalKey = thing.GlobalKey,
            DefName = thing.DefName,
            StackCount = thing.StackCount,
            Quality = thing.Quality,
            HitPoints = thing.HitPoints,
            StuffDefName = thing.StuffDefName,
            MaxHitPoints = thing.MaxHitPoints,
            MinifiedInnerDefName = thing.MinifiedInnerDefName,
            MinifiedInnerStuffDefName = thing.MinifiedInnerStuffDefName,
            MinifiedInnerQuality = thing.MinifiedInnerQuality,
            MinifiedInnerHitPoints = thing.MinifiedInnerHitPoints,
            MinifiedInnerMaxHitPoints = thing.MinifiedInnerMaxHitPoints,
            WornByCorpse = thing.WornByCorpse,
            Biocoded = thing.Biocoded,
            BiocodedPawnLabel = thing.BiocodedPawnLabel,
            BiocodedPawnGlobalId = thing.BiocodedPawnGlobalId,
            DisplayLabel = thing.DisplayLabel,
            MarketValue = thing.MarketValue,
            UniqueWeapon = thing.UniqueWeapon,
            UniqueWeaponTraits = thing.UniqueWeaponTraits.ToList(),
            ThingPackage = thing.ThingPackage,
            ThingPackageId = thing.ThingPackageId,
            PawnPackageId = thing.PawnPackageId,
            Metadata = thing.Metadata?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal) ?? new Dictionary<string, string?>()
        };
    }

    private string? BuildBiocodedPawnGlobalId(Pawn? pawn)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(pawn.ThingID))
        {
            return null;
        }

        return PawnGlobalIdUtility.Build(mod.UserId, pawn);
    }

    private sealed class ThingReferenceComparer : IEqualityComparer<Thing>
    {
        public bool Equals(Thing? x, Thing? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(Thing obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
