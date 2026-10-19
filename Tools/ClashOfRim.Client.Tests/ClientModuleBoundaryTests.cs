using AIRsLight.ClashOfRim.Multiplayer.Diagnostics;
using AIRsLight.ClashOfRim.Achievements;
using AIRsLight.ClashOfRim.Account;
using System;
using System.Linq;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Bank;
using ClashOfRim.TestSupport;

internal static class ClientModuleBoundaryTests
{
    internal static void Run()
    {
        var modules = typeof(ClashOfRimMod).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("AIRsLight.ClashOfRim.Modules.", StringComparison.Ordinal) == true
                || t == typeof(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy)).ToArray();
        var violations = ModuleDependencyGuard.FindViolations(modules, t => t == typeof(ClashOfRimMod)
            || t == typeof(ClashOfRimSettings) || t == typeof(ClashOfRimModNetworkClient) || t == typeof(IServiceProvider)
            || t == typeof(ClashOfRimGameComponent) || t == typeof(ClashOfRimText)
            || t == typeof(AIRsLight.ClashOfRim.ThirdPartyCompatibility.ClashOfRimCompatibilityApi)
            || t.FullName == "AIRsLight.ClashOfRim.DlcCompatibility.BiotechCompatibility"
            || t == typeof(ClientMainThreadDispatcher)
            || t == typeof(AIRsLight.ClashOfRim.RemoteMaps.RemoteSnapshotDownloadService)
            || t == typeof(AIRsLight.ClashOfRim.RemoteMaps.RemoteSnapshotDownloadValidator)
            || t == typeof(AIRsLight.ClashOfRim.RemoteMaps.ClientRemoteMapCloseOperation)
            || t == typeof(AIRsLight.ClashOfRim.RemoteMaps.ClientRemoteSessionRuntime)
            || t == typeof(AIRsLight.ClashOfRim.RemoteMaps.RemoteMapSessionController)
            || t == typeof(AIRsLight.ClashOfRim.RemoteMaps.RemoteSessionMapUtility)
            || t == typeof(AIRsLight.ClashOfRim.RemoteMaps.ClientObservationRuntime)
            || t == typeof(AIRsLight.ClashOfRim.RemoteMaps.ClientObservationPresentation)
            || t == typeof(ClientObservationTransport)
            || t == typeof(ClientRemoteSnapshotTransport) || t == typeof(ClientRaidTransport)
            || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidPreparationPresentation)
            || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidCommandSource)
            || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidLaunchRuntime) || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidLaunchPresentation)
            || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidSupportLossSource) || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidSupportLossPresentation)
            || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidSettlementRuntime) || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidSettlementPresentation)
            || t == typeof(AIRsLight.ClashOfRim.Raids.RaidBattleSnapshotPreparer)
            || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidRecoveryRuntime) || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidRecoveryPresentation)
            || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidBattleDeadlines)
            || t == typeof(AIRsLight.ClashOfRim.RemoteMaps.ClientRemoteMapLongEvent)
            || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidAttackForce) || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidBattlefield)
            || t == typeof(AIRsLight.ClashOfRim.Raids.IRaidAttackForceSource) || t == typeof(AIRsLight.ClashOfRim.Raids.RaidAttackPawnLoad)
            || t == typeof(AIRsLight.ClashOfRim.Raids.CaravanRaidAttackForceSource) || t == typeof(AIRsLight.ClashOfRim.Raids.TransporterRaidAttackForceSource)
            || t == typeof(ClientSnapshotReloadRuntime) || t == typeof(ClientSnapshotReloadPresentation) || t == typeof(ServerSessionGameDataLoader)
            || t == typeof(ServerSnapshotRestoreService) || t == typeof(ClientSnapshotRestorePresentation)
            || t == typeof(ModSnapshotPackageBuilder) || t == typeof(SnapshotSaveSanitizer)
            || t == typeof(ModSnapshotUploadService) || t == typeof(ClientSnapshotPreparationAdapter)
            || t == typeof(ClientSnapshotPackagePresentation) || t == typeof(ClientSnapshotUploadPresentation)
            || t == typeof(ClientLocalSnapshotPresentation)
            || t == typeof(ClientSnapshotPreparationPresentation) || t == typeof(ClientSnapshotFailureView)
            || t == typeof(ClientEventConfirmationEffects) || t == typeof(ClientEventConfirmationPresentation) || t == typeof(SnapshotConfirmationFailureWindow)
            || t == typeof(ClientEventLetterPolicy) || t == typeof(ClientEventLetterRenderer)
            || t == typeof(ClientEventLetterRuntime) || t == typeof(ClientDiplomacyPresentation)
            || t == typeof(ClientEventDecisionPresentation) || t == typeof(ClientEventDecisionRuntime)
            || t == typeof(ClientDiplomacyCreationPresentation) || t == typeof(ClientDiplomacyRelationEffects) || t == typeof(ClientDiplomacyTransport)
            || t == typeof(ClientPasswordStore) || t == typeof(ClientPasswordTransport) || t == typeof(ClientPasswordPresentation)
            || t == typeof(ClientSessionLoginTransport) || t == typeof(ClientSessionStartupRuntime) || t == typeof(ClientSessionStartupPresentation)
            || t == typeof(ClientChatTransport) || t == typeof(ClientChatPresentation)
            || t == typeof(ClientSessionRecoveryRuntime) || t == typeof(ClientSessionRecoveryPresentation) || t == typeof(ClientSessionRecoveryEffects) || t == typeof(SessionExpiredWindow)
            || t == typeof(ClientSessionNotificationEffects) || t == typeof(ClientSessionNotificationPresentation) || t == typeof(ClientConnectionStatusPresentation)
            || t == typeof(ClientSessionExitResources) || t == typeof(ClientSessionIdentityStore)
            || t == typeof(ClientSessionFollowupText) || t == typeof(ClientSnapshotWorldPresentation) || t == typeof(ClientSnapshotSavingPresentation)
            || t == typeof(ClientSnapshotTransactionPresentation) || t == typeof(ClientDiagnosticsText)
            || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClientWorldSetupDiagnostics)
            || t == typeof(AIRsLight.ClashOfRim.CompatibilityClient.ClientCompatibilityOverrideTransport)
            || t == typeof(AIRsLight.ClashOfRim.CompatibilityClient.ClientCompatibilityOverridePresentation)
            || t == typeof(AIRsLight.ClashOfRim.CompatibilityClient.ClientCompatibilityBaselineRuntime)
            || t == typeof(AIRsLight.ClashOfRim.CompatibilityClient.ClientLocalCompatibilityManifest)
            || t == typeof(ClientInitialWorldPresentation) || t == typeof(AIRsLight.ClashOfRim.Admin.ClientAdminBaselineTransport)
            || t == typeof(AIRsLight.ClashOfRim.Admin.ClientAdminBaselineRuntime)
            || t == typeof(AIRsLight.ClashOfRim.Admin.ClientAdminTransport) || t == typeof(AIRsLight.ClashOfRim.Admin.ClientAdminPresentation)
            || t == typeof(ClientWorldWritePresentation) || t == typeof(ClientWorldCatalogRuntime) || t == typeof(ClientWorldCatalogPresentation)
            || t == typeof(ClientWorldMarkerTransport) || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientWorldSynchronizationRuntime)
            || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientWorldMarkersRuntime) || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientWorldMarkersPresentation)
            || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyAppearanceStore)
            || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyAppearanceTarget) || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyAppearancePresentation)
            || t == typeof(ColonyAppearanceDialogWindow)
            || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyAbandonTarget) || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyAbandonPresentation)
            || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyRelocationCommands) || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyRelocationTarget)
            || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyRelocationPresentation) || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ColonyRelocationConfirmationWindow)
            || t == typeof(AIRsLight.ClashOfRim.WorldObjects.AbandonPlayerColonyConfirmationWindow)
            || t == typeof(ClientColonyTransport) || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyRegistrationRuntime)
            || t == typeof(ClientOccupiedColonySitesRuntime) || t == typeof(ClientWorldColonySitesRuntime) || t == typeof(ClientWorldBaselineRuntime) || t == typeof(ClientWorldFeaturesRuntime) || t == typeof(ClientWorldNameRuntime)
            || t == typeof(ClientWorldConfigurationRuntime) || t == typeof(ClientWorldGenerationRuntime) || t == typeof(ClientWorldGenerationPage) || t == typeof(ClientWorldGenerationPresentation)
            || t == typeof(ClientStorytellerRuntime) || t == typeof(ClientStorytellerPage) || t == typeof(ClientWorldRuntimeValues)
            || t == typeof(ClientWorldSourceGameRuntime) || t == typeof(ClientCompatibilityPresentation) || t == typeof(ClientWorldEntryStore) || t == typeof(ClientWorldPreparationTransport) || t == typeof(ClientWorldEntryPresentation)
            || t == typeof(ClientAchievementTransport) || t == typeof(ClientAchievementPresentation) || t == typeof(ClientSessionReconnectTransport)
            || t == typeof(ClientEndgameRuntime) || t == typeof(PendingAchievementCandidates)
            || t == typeof(ClientPlayerDirectoryPresentation) || t == typeof(ClientPlayerDirectoryRuntime) || t == typeof(ClientPlayerSelectionStore)
            || t == typeof(ClientEventReadPresentation) || t == typeof(ClashOfRimEventLetterProjection)
            || t == typeof(ClientEventApplicationRuntime) || t == typeof(ClientEventApplicationPresentation)
            || t == typeof(ItemDeliveryClientProcessor) || t == typeof(GiftLandingApplicator)
            || t == typeof(AIRsLight.ClashOfRim.Support.SupportPawnApplicator) || t == typeof(AIRsLight.ClashOfRim.Raids.RaidAttackerLossApplicator)
            || t == typeof(AIRsLight.ClashOfRim.Pawns.PawnFlowFailurePolicy)
            || t == typeof(ClientGiftPackageHydration) || t == typeof(PawnPackageHydration)
            || t == typeof(ClientGiftActions) || t == typeof(ClientGiftPresentation) || t == typeof(ClientCaravanGiftSource) || t == typeof(ClientTransportPodGiftSource)
            || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientWorldTargetPresentation)
            || t == typeof(ClientBankRuntime) || t == typeof(ClientBankPresentation) || t == typeof(BankUiUtility)
            || t == typeof(AIRsLight.ClashOfRim.Trades.ClientTradeMarketPresentation)
            || t == typeof(ClientTradeMarketTransport)
            || t == typeof(ClientShopTransport) || t == typeof(AIRsLight.ClashOfRim.ServerShop.ClientShopPresentation)
            || t == typeof(AIRsLight.ClashOfRim.Mercenaries.ClientMercenaryRuntime) || t == typeof(AIRsLight.ClashOfRim.Mercenaries.ClientMercenarySource)
            || t == typeof(AIRsLight.ClashOfRim.Mercenaries.ClientMercenaryIncidentRuntime)
            || t == typeof(ClientMercenaryTransport) || t == typeof(AIRsLight.ClashOfRim.Mercenaries.ClientMercenaryPresentation)
            || t == typeof(AIRsLight.ClashOfRim.ServerShop.ClientShopRuntime) || t == typeof(AIRsLight.ClashOfRim.ServerShop.ClientShopSource)
            || t == typeof(AIRsLight.ClashOfRim.ServerShop.ClientShopListingRuntime)
            || t == typeof(ClientTradeOperationsTransport) || t == typeof(PawnPackageTransferService) || t == typeof(PawnExchangeCapture)
            || t == typeof(ClientSupportTransport) || t == typeof(AIRsLight.ClashOfRim.Support.ClientSupportSource)
            || t == typeof(AIRsLight.ClashOfRim.Support.ClientSupportReturnRuntime) || t == typeof(AIRsLight.ClashOfRim.Support.SupportPawnLookup)
            || t == typeof(AIRsLight.ClashOfRim.Support.ClientSupportActions) || t == typeof(AIRsLight.ClashOfRim.Support.ClientSupportPresentation)
            || t == typeof(AIRsLight.ClashOfRim.Trades.ClientTradeActions) || t == typeof(AIRsLight.ClashOfRim.Trades.ClientTradeOperationsPresentation)
            || t == typeof(AIRsLight.ClashOfRim.Trades.ClientTradeDraftSource) || t == typeof(AIRsLight.ClashOfRim.Trades.ClientTradeCaravanSource)
            || t == typeof(AIRsLight.ClashOfRim.Trades.ClientTradeDropPodSource)
            || t.Namespace?.StartsWith("AIRsLight.ClashOfRim.Multiplayer.", StringComparison.Ordinal) == true
            || t.Namespace?.Split('.')[0] is "Verse" or "RimWorld" or "UnityEngine");
        if (modules.Length == 0 || violations.Count != 0)
            throw new InvalidOperationException("Client module boundaries: " + string.Join(Environment.NewLine, violations));
        Console.WriteLine($"Client module dependency guard checked {modules.Length} product and generated types.");
        var compatibilityCapabilities = typeof(ClashOfRimMod).Assembly.GetTypes()
            .Where(t => t.Namespace is "AIRsLight.ClashOfRim.ThirdPartyCompatibility.Things"
                or "AIRsLight.ClashOfRim.ThirdPartyCompatibility.Pawns"
                or "AIRsLight.ClashOfRim.ThirdPartyCompatibility.RemoteMaps"
                or "AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items"
                or "AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes"
                or "AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Pawns"
                or "AIRsLight.ClashOfRim.DlcCompatibility.Biotech.RemoteMaps"
                or "AIRsLight.ClashOfRim.DlcCompatibility.Ideology.Ideos").ToArray();
        var compatibilityViolations = ModuleDependencyGuard.FindViolations(compatibilityCapabilities,
            t => t == typeof(AIRsLight.ClashOfRim.ThirdPartyCompatibility.ClashOfRimCompatibilityApi)
                || t.FullName == "AIRsLight.ClashOfRim.DlcCompatibility.BiotechCompatibility"
                || t == typeof(AIRsLight.ClashOfRim.DlcCompatibility.RemoteIdeoMaterializer)
                || t == typeof(AIRsLight.ClashOfRim.DlcCompatibility.RemoteIdeoCatalog)
                || t == typeof(ClashOfRimMod) || t == typeof(ClashOfRimSettings)
                || t == typeof(ClashOfRimModNetworkClient) || t == typeof(ClashOfRimGameComponent)
                || t == typeof(IServiceProvider) || t == typeof(Verse.LoadedModManager) || t == typeof(ClashLog));
        if (compatibilityCapabilities.Length == 0 || compatibilityViolations.Count != 0)
            throw new InvalidOperationException("Compatibility capability boundaries: " + string.Join(Environment.NewLine, compatibilityViolations));
        Console.WriteLine($"Compatibility capability guard checked {compatibilityCapabilities.Length} product and generated types.");
        Type[] ideologyRoots = { typeof(AIRsLight.ClashOfRim.DlcCompatibility.ClientSharedIdeologyWorld),
            typeof(AIRsLight.ClashOfRim.DlcCompatibility.ClientIdeologyReforms),
            typeof(AIRsLight.ClashOfRim.DlcCompatibility.IdeoReformSyncPatches),
            typeof(AIRsLight.ClashOfRim.DlcCompatibility.ClassicIdeoSetupPatch),
            typeof(AIRsLight.ClashOfRim.DlcCompatibility.SharedIdeologySetupWorldPatch),
            typeof(AIRsLight.ClashOfRim.DlcCompatibility.PlayerIdeoForkPatches), typeof(ClientMultiplayerWorldStatus) };
        var ideologySetup = typeof(ClashOfRimMod).Assembly.GetTypes()
            .Where(t => ideologyRoots.Contains(t) || (t.DeclaringType is { } owner && ideologyRoots.Contains(owner))).ToArray();
        var ideologyViolations = ModuleDependencyGuard.FindViolations(ideologySetup,
            t => t == typeof(ClashOfRimMod) || t == typeof(ClashOfRimSettings) || t == typeof(ClashOfRimModNetworkClient)
                || t == typeof(AIRsLight.ClashOfRim.ThirdPartyCompatibility.ClashOfRimCompatibilityApi)
                || t == typeof(Verse.LoadedModManager) || t == typeof(IServiceProvider));
        if (ideologyViolations.Count != 0)
            throw new InvalidOperationException("Ideology setup boundaries: " + string.Join(Environment.NewLine, ideologyViolations));
        Console.WriteLine($"Ideology setup guard checked {ideologySetup.Length} product and generated types.");
        var pages = typeof(ClashOfRimMod).Assembly.GetTypes()
            .Where(t => t.Namespace is "AIRsLight.ClashOfRim.Multiplayer.Pages" or "AIRsLight.ClashOfRim.Multiplayer.Players"
                or "AIRsLight.ClashOfRim.Multiplayer.Achievements" or "AIRsLight.ClashOfRim.Multiplayer.Diagnostics"
                || t == typeof(AIRsLight.ClashOfRim.Multiplayer.AllySupportRequestDialogWindow)
                || t == typeof(AIRsLight.ClashOfRim.Support.QuestPart_ClashSupportPawn)
                || t == typeof(AIRsLight.ClashOfRim.Support.ClientSupportQuestCallbacks)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.Support.ClientSupportQuestCallbacks)
                || t == typeof(AIRsLight.ClashOfRim.ServerShop.ClientShopListingRuntime)
                || t == typeof(AIRsLight.ClashOfRim.Trades.ClientPawnPreviewRuntime)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientTransporterActions)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyGizmos)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyGizmos)
                || t == typeof(PlayerColonyAppearanceGizmoPatch) || t.DeclaringType == typeof(PlayerColonyAppearanceGizmoPatch)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.PlayerColonyAbandonCommandPatch)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClientStorytellerEditor)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.MainMenu.ClientStorytellerEditor)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimStorytellerInGamePostClosePatch)
                || t == typeof(ClientWorldGenerationPage) || t == typeof(ClientStorytellerPage)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClientWorldSiteReservations)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimCreateWorldParamsPatches)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimStorytellerSelectionPatches)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimStorytellerCommitmentModePatch)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimStartingSiteValidationPatch)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimNewSiteTileReservationPatch)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimTraversalTileReservationPatch)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClientWorldSetupPages)
                || t == typeof(AIRsLight.ClashOfRim.ClientNetwork.ClientAutomaticSessionStartup)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientWorldSynchronizationCallbacks)
                || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidLifecycleCallbacks)
                || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidFinishActions)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidFinishActions)
                || t == typeof(AIRsLight.ClashOfRim.Raids.ClientRaidBattleOverlay)
                || t == typeof(AIRsLight.ClashOfRim.Chat.ClientChatOverlay)
                || t == typeof(AIRsLight.ClashOfRim.ClientSnapshots.ClientSnapshotSavingCallbacks)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.ClientSnapshots.ClientSnapshotSavingCallbacks)
                || t == typeof(AIRsLight.ClashOfRim.ClientSnapshots.AutosaveSnapshotUploadPatch)
                || t == typeof(AIRsLight.ClashOfRim.ClientNetwork.ClientSessionExitCallbacks)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimGoToMainMenuDisconnectPatch)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimShutdownDisconnectPatch)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClientServerEntryWindows)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.MainMenu.ClientServerEntryWindows)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimMainMenuPatches)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimMainMenuPatches)
                || t == typeof(AIRsLight.ClashOfRim.ThirdPartyCompatibility.ClashOfRimCompatibilityApi)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.ThirdPartyCompatibility.ClashOfRimCompatibilityApi)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.ClientNetwork.ClientAutomaticSessionStartup)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.MainMenu.ClientWorldSetupPages)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimWorldSetupPatches)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimStartingSitePostOpenPatch)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimStartingSiteExtraOnGuiPatch)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimStartingSitePostClosePatch)
                || t == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimWorldTileReservationUtility)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.MainMenu.ClashOfRimWorldTileReservationUtility)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonySiteEvents)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonySiteEvents)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.PlayerColonySiteSettledPatch)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.PlayerColonySiteRemovedPatch)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyRelocationCommands)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyRelocationCommands)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ColonyRelocationSettleCommandPatch)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ColonyRelocationExistingMapSettleCommandPatch)
                || t == typeof(AIRsLight.ClashOfRim.DlcCompatibility.RemoteSessionOdysseyGravshipTakeoffPatch)
                || t == typeof(AIRsLight.ClashOfRim.DlcCompatibility.OdysseyGravshipLandingSnapshotPatch)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientWorldObjectCommandRuntime)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.ClientWorldObjectCommandTarget)
                || t == typeof(AIRsLight.ClashOfRim.WorldObjects.RemoteWorldObjectUiUtility)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.WorldObjects.RemoteWorldObjectUiUtility)
                || t == typeof(AIRsLight.ClashOfRim.Gifts.RemoteGiftTransportersArrivalAction)
                || t == typeof(AIRsLight.ClashOfRim.Gifts.RemoteGiftArrivalGuard)
                || t == typeof(AIRsLight.ClashOfRim.Raids.RemoteRaidTransportersArrivalAction)
                || t == typeof(AIRsLight.ClashOfRim.Pawns.PawnScribeLoader)
                || t == typeof(AIRsLight.ClashOfRim.Pawns.PawnPreviewScribeRestorer)
                || t == typeof(AIRsLight.ClashOfRim.Pawns.PawnPreviewIdeoTracker)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.Trades.ClientPawnPreviewRuntime)
                || t == typeof(AIRsLight.ClashOfRim.Trades.ClientPawnPreviewTransport)
                || t == typeof(AIRsLight.ClashOfRim.Trades.TradeUiUtility)
                || t == typeof(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityBaselineOverrideWindow)
                || t == typeof(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityMismatchWindow)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityMismatchWindow)
                || t == typeof(AIRsLight.ClashOfRim.CompatibilityClient.ClientCompatibilityRepairRuntime)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityBaselineOverrideWindow)
                || t.DeclaringType == typeof(AIRsLight.ClashOfRim.ServerShop.ClientShopListingRuntime)).ToArray();
        var pageViolations = ModuleDependencyGuard.FindViolations(pages,
            t => t == typeof(ClashOfRimMod) || t == typeof(ClashOfRimSettings) || t == typeof(ClashOfRimModNetworkClient)
                || t == typeof(AIRsLight.ClashOfRim.Multiplayer.ClashOfRimMultiplayerWindow)
                || t == typeof(Verse.LoadedModManager) || t == typeof(IServiceProvider));
        if (pages.Length == 0 || pageViolations.Count != 0)
            throw new InvalidOperationException("Multiplayer page boundaries: " + string.Join(Environment.NewLine, pageViolations));
        Console.WriteLine($"Multiplayer page guard checked {pages.Length} product and generated types.");
        var sessionModules = modules.Where(t => t.Namespace == "AIRsLight.ClashOfRim.Modules.Sessions").ToArray();
        var sessionViolations = ModuleDependencyGuard.FindViolations(sessionModules,
            t => t.Namespace?.StartsWith("AIRsLight.ClashOfRim.Modules.Snapshots", StringComparison.Ordinal) == true
                || t.Namespace?.StartsWith("AIRsLight.ClashOfRim.Modules.Events", StringComparison.Ordinal) == true);
        if (sessionViolations.Count != 0)
            throw new InvalidOperationException("Session foundation depends on an event/snapshot domain: " + string.Join(Environment.NewLine, sessionViolations));
        Type[] eventAdapters = { typeof(ClientPasswordTransport), typeof(ClientPasswordPresentation), typeof(AIRsLight.ClashOfRim.RemoteMaps.ClientRemoteMapCloseOperation),
            typeof(AIRsLight.ClashOfRim.RemoteMaps.ClientRemoteSessionRuntime), typeof(AIRsLight.ClashOfRim.RemoteMaps.RemoteSessionMapUtility), typeof(AIRsLight.ClashOfRim.RemoteMaps.ClientObservationRuntime), typeof(AIRsLight.ClashOfRim.RemoteMaps.ClientObservationPresentation),
            typeof(AIRsLight.ClashOfRim.Raids.ClientRaidPreparationPresentation),
            typeof(AIRsLight.ClashOfRim.Raids.ClientRaidCommandSource), typeof(AIRsLight.ClashOfRim.Raids.ClientRaidLaunchRuntime), typeof(AIRsLight.ClashOfRim.Raids.ClientRaidLaunchPresentation),
            typeof(AIRsLight.ClashOfRim.Raids.ClientRaidSupportLossSource), typeof(AIRsLight.ClashOfRim.Raids.ClientRaidSupportLossPresentation),
            typeof(AIRsLight.ClashOfRim.Raids.ClientRaidSettlementRuntime), typeof(AIRsLight.ClashOfRim.Raids.ClientRaidSettlementPresentation),
            typeof(AIRsLight.ClashOfRim.Raids.ClientRaidRecoveryRuntime), typeof(AIRsLight.ClashOfRim.Raids.ClientRaidRecoveryPresentation),
            typeof(AIRsLight.ClashOfRim.Raids.ClientRaidBattleDeadlines),
            typeof(AIRsLight.ClashOfRim.RemoteMaps.ClientRemoteMapLongEvent),
            typeof(AIRsLight.ClashOfRim.Raids.ClientRaidAttackForce), typeof(AIRsLight.ClashOfRim.Raids.ClientRaidBattlefield),
            typeof(AIRsLight.ClashOfRim.Raids.CaravanRaidAttackForceSource), typeof(AIRsLight.ClashOfRim.Raids.TransporterRaidAttackForceSource),
            typeof(AIRsLight.ClashOfRim.Support.ClientSupportSource), typeof(AIRsLight.ClashOfRim.Support.ClientSupportActions), typeof(AIRsLight.ClashOfRim.Support.ClientSupportPresentation),
            typeof(AIRsLight.ClashOfRim.Support.ClientSupportReturnRuntime), typeof(AIRsLight.ClashOfRim.Support.SupportPawnLookup),
            typeof(AIRsLight.ClashOfRim.ServerShop.ClientShopRuntime), typeof(AIRsLight.ClashOfRim.ServerShop.ClientShopSource),
            typeof(AIRsLight.ClashOfRim.Mercenaries.ClientMercenaryPresentation), typeof(AIRsLight.ClashOfRim.Mercenaries.ClientMercenaryIncidentRuntime), typeof(AIRsLight.ClashOfRim.Mercenaries.ClientMercenaryRuntime), typeof(AIRsLight.ClashOfRim.Mercenaries.ClientMercenarySource),
            typeof(AIRsLight.ClashOfRim.ServerShop.ClientShopPresentation), typeof(AIRsLight.ClashOfRim.Trades.ClientTradeActions), typeof(AIRsLight.ClashOfRim.Trades.ClientTradeOperationsPresentation),
            typeof(AIRsLight.ClashOfRim.Trades.ClientTradeDraftSource), typeof(AIRsLight.ClashOfRim.Trades.ClientTradeCaravanSource), typeof(AIRsLight.ClashOfRim.Trades.ClientTradeDropPodSource),
            typeof(PawnExchangeCapture), typeof(PawnPackageTransferService), typeof(AIRsLight.ClashOfRim.Trades.ClientTradeMarketPresentation), typeof(ClientBankRuntime), typeof(ClientBankPresentation), typeof(ClientGiftActions), typeof(ClientGiftPresentation), typeof(ClientCaravanGiftSource), typeof(ClientTransportPodGiftSource), typeof(ClientEventLetterPolicy), typeof(ClientEventLetterRenderer), typeof(ClientEventLetterRuntime),
            typeof(ClientDiplomacyPresentation), typeof(ClientEventTextFormatting), typeof(ClientEventReadPresentation),
            typeof(ClientEventDecisionPresentation), typeof(ClientEventDecisionRuntime),
            typeof(ClientDiplomacyCreationPresentation), typeof(ClientDiplomacyRelationEffects), typeof(ClientDiplomacyTransport),
            typeof(ClientSessionLoginTransport), typeof(ClientSessionStartupRuntime), typeof(ClientSessionStartupPresentation),
            typeof(ClientChatTransport), typeof(ClientChatPresentation),
            typeof(ClientSessionRecoveryRuntime), typeof(ClientSessionRecoveryPresentation), typeof(ClientSessionRecoveryEffects), typeof(SessionExpiredWindow),
            typeof(ClientSessionNotificationEffects), typeof(ClientSessionNotificationPresentation), typeof(ClientConnectionStatusPresentation),
            typeof(ClientSessionExitResources),
            typeof(ClientSessionFollowupText), typeof(ClientSnapshotWorldPresentation), typeof(ClientSnapshotSavingPresentation),
            typeof(ClientSnapshotTransactionPresentation), typeof(ClientDiagnosticsText), typeof(AIRsLight.ClashOfRim.MainMenu.ClientWorldSetupDiagnostics),
            typeof(AIRsLight.ClashOfRim.CompatibilityClient.ClientCompatibilityOverrideTransport),
            typeof(AIRsLight.ClashOfRim.CompatibilityClient.ClientCompatibilityOverridePresentation),
            typeof(AIRsLight.ClashOfRim.CompatibilityClient.ClientCompatibilityBaselineRuntime),
            typeof(AIRsLight.ClashOfRim.CompatibilityClient.ClientLocalCompatibilityManifest),
            typeof(ClientInitialWorldPresentation), typeof(AIRsLight.ClashOfRim.Admin.ClientAdminBaselineTransport),
            typeof(AIRsLight.ClashOfRim.Admin.ClientAdminBaselineRuntime),
            typeof(AIRsLight.ClashOfRim.Admin.ClientAdminTransport), typeof(AIRsLight.ClashOfRim.Admin.ClientAdminPresentation),
            typeof(ClientWorldWritePresentation), typeof(ClientWorldCatalogRuntime), typeof(ClientWorldCatalogPresentation),
            typeof(ClientWorldMarkerTransport), typeof(AIRsLight.ClashOfRim.WorldObjects.ClientWorldSynchronizationRuntime),
            typeof(AIRsLight.ClashOfRim.WorldObjects.ClientWorldMarkersRuntime), typeof(AIRsLight.ClashOfRim.WorldObjects.ClientWorldMarkersPresentation),
            typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyAppearanceTarget), typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyAppearancePresentation), typeof(ColonyAppearanceDialogWindow),
            typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyAbandonTarget), typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyAbandonPresentation), typeof(AIRsLight.ClashOfRim.WorldObjects.AbandonPlayerColonyConfirmationWindow),
            typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyRelocationCommands), typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyRelocationTarget),
            typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyRelocationPresentation), typeof(AIRsLight.ClashOfRim.WorldObjects.ColonyRelocationConfirmationWindow),
            typeof(ClientColonyTransport), typeof(AIRsLight.ClashOfRim.WorldObjects.ClientColonyRegistrationRuntime),
            typeof(ClientOccupiedColonySitesRuntime), typeof(ClientWorldColonySitesRuntime), typeof(ClientWorldBaselineRuntime), typeof(ClientWorldFeaturesRuntime), typeof(ClientWorldNameRuntime),
            typeof(ClientWorldConfigurationRuntime), typeof(ClientWorldGenerationRuntime), typeof(ClientWorldGenerationPage), typeof(ClientWorldGenerationPresentation),
            typeof(ClientStorytellerRuntime), typeof(ClientStorytellerPage), typeof(ClientWorldRuntimeValues),
            typeof(ClientWorldSourceGameRuntime), typeof(ClientCompatibilityPresentation), typeof(ClientWorldPreparationTransport), typeof(ClientWorldEntryPresentation),
            typeof(ClientAchievementPresentation), typeof(ClientAchievementTransport), typeof(ClientSessionReconnectTransport), typeof(ClientEndgameRuntime), typeof(PendingAchievementCandidates),
            typeof(ClientPlayerDirectoryPresentation), typeof(ClientPlayerDirectoryRuntime),
            typeof(ClientGiftPackageHydration), typeof(PawnPackageHydration),
            typeof(ClientEventApplicationRuntime), typeof(ClientEventApplicationPresentation),
            typeof(ClientEventConfirmationPresentation), typeof(ClientEventConfirmationEffects), typeof(ClientSnapshotPreparationPresentation),
            typeof(ClientSnapshotRestorePresentation), typeof(ServerSnapshotRestoreService),
            typeof(ClientSnapshotReloadRuntime), typeof(ClientSnapshotReloadPresentation), typeof(ServerSessionGameDataLoader),
            typeof(ClientLocalSnapshotPresentation), typeof(ClientSnapshotFailureView), typeof(SnapshotConfirmationFailureWindow) };
        var adapterTypes = typeof(ClashOfRimMod).Assembly.GetTypes().Where(t => eventAdapters.Contains(t)
            || (t.DeclaringType is not null && eventAdapters.Contains(t.DeclaringType))).ToArray();
        var adapterViolations = ModuleDependencyGuard.FindViolations(adapterTypes, t => t == typeof(ClashOfRimMod)
            || t == typeof(ClashOfRimSettings) || t == typeof(ClashOfRimModNetworkClient) || t == typeof(IServiceProvider)
            || t == typeof(AIRsLight.ClashOfRim.RemoteMaps.RemoteMapSessionController));
        if (adapterViolations.Count != 0)
            throw new InvalidOperationException("Event adapters depend on the legacy host: " + string.Join(Environment.NewLine, adapterViolations));
        Console.WriteLine($"Event adapter dependency guard checked {adapterTypes.Length} product and generated types.");
    }
}
