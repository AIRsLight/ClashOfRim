using AIRsLight.ClashOfRim.ThirdPartyCompatibility.Things;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    private static readonly List<RaidCaravanMapEntryHandler> RaidCaravanMapEntryHandlers = new();
    private static readonly List<RemoteDefenderMapPreparedHandler> RemoteDefenderMapPreparedHandlers = new();
    private static readonly List<RemoteMapLoadedHandler> RemoteMapLoadedHandlers = new();
    private static readonly List<RemoteMapProjectionReferenceRewriter> RemoteMapProjectionReferenceRewriters = new();
    private static readonly List<RemoteMapProjectionSanitizer> RemoteMapProjectionSanitizers = new();
    private static readonly List<RemoteSessionPawnCleanupHandler> RemoteSessionPawnCleanupHandlers = new();
    private static readonly List<RemoteMapIntegerLoadIdRewriterRegistration> RemoteMapIntegerLoadIdRewriters = new();
    private static readonly List<RemoteMapAreaLoadIdProvider> RemoteMapAreaLoadIdProviders = new();
    private static readonly List<CompatibilitySnapshotSaveSanitizer> SnapshotSaveSanitizers = new();
    private static readonly List<PlayerColonySiteRegistrationSuppressor> PlayerColonySiteRegistrationSuppressors = new();
    private static readonly List<AdminBaselineExtensionProviderRegistration> AdminBaselineExtensionProviders = new();
    private static readonly List<CompatibilityPackageRegistration> CompatibilityPackages = new();
    private static readonly List<WorldConfigurationExtensionHandlerRegistration> WorldConfigurationExtensionHandlers = new();
    private static readonly List<WorldConfigurationExtensionSummaryProvider> WorldConfigurationExtensionSummaryProviders = new();
    private static readonly List<WorldGenerationFloatSettingProviderRegistration> WorldGenerationFloatSettingProviders = new();
    private static readonly List<FactionPreparedHandler> FactionPreparedHandlers = new();
    private static readonly List<PlayerProxyFactionPreparedHandler> PlayerProxyFactionPreparedHandlers = new();
    private static readonly List<PawnReferenceMetadataCollector> PawnReferenceMetadataCollectors = new();
    private static readonly List<PawnReferenceMetadataResolverRegistration> PawnReferenceMetadataResolvers = new();
    private static readonly List<PawnReferenceMetadataRestorerRegistration> PawnReferenceMetadataRestorers = new();
    private static readonly List<PawnExchangeExtensionAppender> PawnExchangeExtensionAppenders = new();
    private static readonly List<PawnExchangePackageRegistrar> PawnExchangePackageRegistrars = new();
    private static readonly List<PawnExchangePackageNormalizer> PawnExchangePackageNormalizers = new();
    private static readonly List<PawnExchangeLocalSaveOnlyLoadIdPredicate> PawnExchangeLocalSaveOnlyLoadIdPredicates = new();
    private static readonly List<PawnPostRestoreLocalizer> PawnPostRestoreLocalizers = new();
    private static readonly List<PawnSoldEffectHandler> PawnSoldEffectHandlers = new();
    private static readonly List<TradeablePawnPredicate> TradeablePawnPredicates = new();
    private static readonly List<TradePawnRestoreValidator> TradePawnRestoreValidators = new();
    private static readonly CompatibilityRegistrationScope Registrations = new(message => Log.Warning(message));
    private static readonly ThingReferenceCompatibility ThingReferences = new(Registrations);
    private static readonly ThingTransferCompatibility ThingTransfers = new(Registrations);
    private static HashSet<string>? serverDlcIds;

    public static bool IsRemoteMapProjectionLoading => RemoteMapProjectionLoadScope.Active;

    public static bool HasSnapshotSaveSanitizers => SnapshotSaveSanitizers.Count > 0;

    public static CompatibilityRegistrationToken BeginRegistrationCycle(string owner) => Registrations.BeginRegistrationCycle(owner);
    public static CompatibilityRegistrationToken BeginRegistrationCycle(string owner, int priority) => Registrations.BeginRegistrationCycle(owner, priority);
    public static int CurrentRegistrationGeneration => Registrations.CurrentRegistrationGeneration;
    public static CompatibilityRegistrationToken CreateRegistrationToken(string owner) => Registrations.CreateRegistrationToken(owner);
    public static CompatibilityRegistrationToken CreateRegistrationToken(string owner, int priority) => Registrations.CreateRegistrationToken(owner, priority);
    public static bool UseRegistrationToken(CompatibilityRegistrationToken token, Action registration) => Registrations.UseRegistrationToken(token, registration);
    public static bool RevokeRegistrationToken(CompatibilityRegistrationToken? token) => Registrations.RevokeRegistrationToken(token);
    public static int RevokeRegistrationsByOwner(string owner) => Registrations.RevokeRegistrationsByOwner(owner);

    public static bool IsActiveMultiplayerSession
    {
        get
        {
            try
            {
                ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
                return mod is not null
                    && (mod.IsInActiveMultiplayerSession
                        || mod.CanUseVanillaMenuSnapshotUpload
                        || ClashOfRimGameComponent.HasActiveRemoteMapSession
                        || ClashOfRimGameComponent.HasActiveRaidBattleSession);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsCurrentUserAdministrator
    {
        get
        {
            try
            {
                return LoadedModManager.GetMod<ClashOfRimMod>()?.IsAdministrator == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool RequestServerWorldBaselineRefresh(string reason)
    {
        try
        {
            ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
            if (mod is null)
            {
                return false;
            }

            mod.RequestServerWorldBaselineRefresh(reason);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool RequestServerWorldBaselineRefreshByKey(string translationKey)
    {
        return RequestServerWorldBaselineRefresh(ClashOfRimText.Key(translationKey));
    }

    private static bool ServerAllowsDlc(string packageId)
    {
        return serverDlcIds is null || serverDlcIds.Contains(packageId);
    }

}
