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
    private static readonly List<ThingReferenceEditorRegistration> ThingReferenceEditorRegistrations = new();
    private static readonly List<ThingReferenceDefaultMetadataProvider> ThingReferenceDefaultMetadataProviders = new();
    private static readonly List<ThingReferenceUniqueRequestPredicate> ThingReferenceUniqueRequestPredicates = new();
    private static readonly List<ThingReferenceMetadataRegistration> ThingReferenceMetadataRegistrations = new();
    private static readonly List<ThingReferenceDisplayRegistration> ThingReferenceDisplayRegistrations = new();
    private static readonly List<ThingReferenceCacheKeyPartsProvider> ThingReferenceCacheKeyPartsProviders = new();
    private static readonly List<ThingReferenceMetadataNormalizer> ThingReferenceMetadataNormalizers = new();
    private static readonly List<ThingReferenceThingFactoryRegistration> ThingReferenceThingFactories = new();
    private static readonly List<ThingReferenceDefKindPredicateRegistration> ThingReferenceDefKindPredicates = new();
    private static readonly List<DefensePointDefNameProvider> DefensePointDefNameProviders = new();
    private static readonly List<CompatibilityRegistrationRecord> RegistrationRecords = new();
    private static readonly List<CompatibilityRegistrationDiagnostic> RegistrationDiagnostics = new();
    private static readonly Dictionary<object, int> RegistrationPriorities = new(new ReferenceEqualityComparer());
    private static readonly Dictionary<object, int> RegistrationOrders = new(new ReferenceEqualityComparer());
    private static readonly Dictionary<int, CompatibilityRegistrationToken> RegistrationTokens = new();
    private static readonly Dictionary<int, List<Action>> RegistrationCleanupsByToken = new();
    private static HashSet<string>? serverDlcIds;
    private static int registrationGeneration;
    private static int registrationTokenSequence;
    private static int registrationOrderSequence;
    private static CompatibilityRegistrationToken? activeRegistrationToken;
    private static ThingReferenceDisplayRegistration[]? cachedThingReferenceDisplayRegistrations;
    private static ThingReferenceCacheKeyPartsProvider[]? cachedThingReferenceCacheKeyPartsProviders;

    public static bool IsRemoteMapProjectionLoading => RemoteMapProjectionLoadScope.Active;

    public static bool HasSnapshotSaveSanitizers => SnapshotSaveSanitizers.Count > 0;

    public static CompatibilityRegistrationToken BeginRegistrationCycle(string owner)
    {
        ClearCompatibilityRegistrations();
        RegistrationTokens.Clear();
        RegistrationCleanupsByToken.Clear();
        registrationGeneration = unchecked(registrationGeneration + 1);
        activeRegistrationToken = CreateRegistrationTokenCore(owner, priority: 0);
        return activeRegistrationToken;
    }

    public static CompatibilityRegistrationToken BeginRegistrationCycle(string owner, int priority)
    {
        ClearCompatibilityRegistrations();
        RegistrationTokens.Clear();
        RegistrationCleanupsByToken.Clear();
        registrationGeneration = unchecked(registrationGeneration + 1);
        activeRegistrationToken = CreateRegistrationTokenCore(owner, priority);
        return activeRegistrationToken;
    }

    public static int CurrentRegistrationGeneration => registrationGeneration;

    public static CompatibilityRegistrationToken CreateRegistrationToken(string owner)
    {
        return CreateRegistrationTokenCore(owner, priority: 0);
    }

    public static CompatibilityRegistrationToken CreateRegistrationToken(string owner, int priority)
    {
        return CreateRegistrationTokenCore(owner, priority);
    }

    public static bool UseRegistrationToken(CompatibilityRegistrationToken token, Action registration)
    {
        if (!IsCurrentRegistrationToken(token) || registration is null)
        {
            return false;
        }

        CompatibilityRegistrationToken? previous = activeRegistrationToken;
        activeRegistrationToken = token;
        try
        {
            registration();
            return true;
        }
        finally
        {
            activeRegistrationToken = previous;
        }
    }

    public static bool RevokeRegistrationToken(CompatibilityRegistrationToken? token)
    {
        if (!IsCurrentRegistrationToken(token))
        {
            return false;
        }

        CompatibilityRegistrationToken currentToken = token!;
        bool hadRegistrations = false;
        if (RegistrationCleanupsByToken.TryGetValue(currentToken.Id, out List<Action> cleanups))
        {
            hadRegistrations = cleanups.Count > 0;
            for (int i = cleanups.Count - 1; i >= 0; i--)
            {
                try
                {
                    cleanups[i]();
                }
                catch (Exception ex)
                {
                    Log.Warning("[ClashOfRim] Failed to revoke compatibility registration for " + currentToken.Owner + ": " + ex.Message);
                }
            }
        }

        RegistrationCleanupsByToken.Remove(currentToken.Id);
        RegistrationTokens.Remove(currentToken.Id);
        if (activeRegistrationToken?.Id == currentToken.Id)
        {
            activeRegistrationToken = null;
        }

        return hadRegistrations;
    }

    public static int RevokeRegistrationsByOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return 0;
        }

        int revoked = 0;
        string normalizedOwner = owner.Trim();
        foreach (CompatibilityRegistrationToken token in RegistrationTokens.Values.ToList())
        {
            if (string.Equals(token.Owner, normalizedOwner, StringComparison.OrdinalIgnoreCase)
                && RevokeRegistrationToken(token))
            {
                revoked++;
            }
        }

        return revoked;
    }

    private static CompatibilityRegistrationToken CreateRegistrationTokenCore(string owner, int priority)
    {
        string normalizedOwner = string.IsNullOrWhiteSpace(owner) ? "unknown" : owner.Trim();
        var token = new CompatibilityRegistrationToken(
            normalizedOwner,
            registrationGeneration,
            unchecked(++registrationTokenSequence),
            priority);
        RegistrationTokens[token.Id] = token;
        RegistrationCleanupsByToken[token.Id] = new List<Action>();
        return token;
    }

    private static bool IsCurrentRegistrationToken(CompatibilityRegistrationToken? token)
    {
        return token is not null
            && token.Generation == registrationGeneration
            && RegistrationTokens.TryGetValue(token.Id, out CompatibilityRegistrationToken registered)
            && ReferenceEquals(registered, token);
    }

    private static void TrackRegistration(Action cleanup)
    {
        CompatibilityRegistrationToken? token = activeRegistrationToken;
        if (!IsCurrentRegistrationToken(token))
        {
            return;
        }

        CompatibilityRegistrationToken currentToken = token!;
        if (!RegistrationCleanupsByToken.TryGetValue(currentToken.Id, out List<Action> cleanups))
        {
            cleanups = new List<Action>();
            RegistrationCleanupsByToken[currentToken.Id] = cleanups;
        }

        cleanups.Add(cleanup);
    }

    private static void RegisterUnique<T>(List<T> registrations, T registration)
        where T : class
    {
        if (registration is null || registrations.Contains(registration))
        {
            return;
        }

        registrations.Add(registration);
        int priority = ActiveRegistrationPriority();
        RegistrationPriorities[registration] = priority;
        RegistrationOrders[registration] = unchecked(++registrationOrderSequence);
        SortRegistrationsByPriority(registrations);
        InvalidateCompatibilityRegistrationSnapshots();
        CompatibilityRegistrationRecord record = AddRegistrationRecord(CompatibilityRegistrationCategory(typeof(T)), RegistrationKey(registration), priority);
        TrackRegistration(() =>
        {
            registrations.Remove(registration);
            RegistrationPriorities.Remove(registration);
            RegistrationOrders.Remove(registration);
            RegistrationRecords.Remove(record);
            InvalidateCompatibilityRegistrationSnapshots();
        });
    }

    private static void RegisterMetadataKeyedRegistration<T>(
        List<T> registrations,
        T registration,
        string category,
        string key,
        string duplicateRejectedCode,
        string duplicateReplacedCode)
        where T : class, IMetadataKeyedRegistration
    {
        if (registration is null || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        int priority = ActiveRegistrationPriority();
        string normalizedKey = key.Trim();
        T? existing = registrations.FirstOrDefault(item =>
            string.Equals(item.MetadataKey, normalizedKey, StringComparison.Ordinal));
        if (existing is not null)
        {
            int existingPriority = RegistrationPriority(existing);
            if (priority <= existingPriority)
            {
                AddRegistrationDiagnostic(
                    "Warning",
                    duplicateRejectedCode,
                    category,
                    normalizedKey,
                    category + " '" + normalizedKey + "' was rejected because an existing registration has priority " + existingPriority + ".");
                return;
            }

            AddRegistrationDiagnostic(
                "Warning",
                duplicateReplacedCode,
                category,
                normalizedKey,
                category + " '" + normalizedKey + "' replaced an existing registration with priority " + existingPriority + ".");
            registrations.Remove(existing);
            RemoveRegistrationTracking(existing, category, normalizedKey);
            InvalidateCompatibilityRegistrationSnapshots();
        }

        registrations.Add(registration);
        RegistrationPriorities[registration] = priority;
        RegistrationOrders[registration] = unchecked(++registrationOrderSequence);
        SortRegistrationsByPriority(registrations);
        InvalidateCompatibilityRegistrationSnapshots();
        CompatibilityRegistrationRecord record = AddRegistrationRecord(category, normalizedKey, priority);
        TrackRegistration(() =>
        {
            registrations.Remove(registration);
            RegistrationPriorities.Remove(registration);
            RegistrationOrders.Remove(registration);
            RegistrationRecords.Remove(record);
            InvalidateCompatibilityRegistrationSnapshots();
        });
    }

    private static int ActiveRegistrationPriority()
    {
        CompatibilityRegistrationToken? token = activeRegistrationToken;
        return IsCurrentRegistrationToken(token) ? token!.Priority : 0;
    }

    private static void SortRegistrationsByPriority<T>(List<T> registrations)
        where T : class
    {
        registrations.Sort((left, right) =>
        {
            int priorityComparison = RegistrationPriority(right).CompareTo(RegistrationPriority(left));
            return priorityComparison != 0
                ? priorityComparison
                : RegistrationOrder(left).CompareTo(RegistrationOrder(right));
        });
    }

    private static int RegistrationPriority(object registration)
    {
        return RegistrationPriorities.TryGetValue(registration, out int priority) ? priority : 0;
    }

    private static int RegistrationOrder(object registration)
    {
        return RegistrationOrders.TryGetValue(registration, out int order) ? order : 0;
    }

    private static void RemoveRegistrationTracking(object registration, string category, string key)
    {
        RegistrationPriorities.Remove(registration);
        RegistrationOrders.Remove(registration);
        RegistrationRecords.RemoveAll(record =>
            string.Equals(record.Category, category, StringComparison.Ordinal)
            && string.Equals(record.Key, key, StringComparison.Ordinal));
    }

    private static CompatibilityRegistrationRecord AddRegistrationRecord(string category, string key, int priority)
    {
        CompatibilityRegistrationToken? token = activeRegistrationToken;
        var record = new CompatibilityRegistrationRecord(
            category,
            string.IsNullOrWhiteSpace(key) ? "<unknown>" : key,
            IsCurrentRegistrationToken(token) ? token!.Owner : "<untracked>",
            IsCurrentRegistrationToken(token) ? token!.Generation : registrationGeneration,
            priority);
        RegistrationRecords.Add(record);
        return record;
    }

    private static void AddRegistrationDiagnostic(
        string severity,
        string code,
        string category,
        string key,
        string message)
    {
        var diagnostic = new CompatibilityRegistrationDiagnostic(
            severity,
            code,
            category,
            key,
            message);
        RegistrationDiagnostics.Add(diagnostic);
        if (string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[ClashOfRim] Compatibility registration " + code + ": " + message);
        }
    }

    private static bool TryInvokeCompatibilityCallback(
        string operation,
        object registration,
        Action callback)
    {
        try
        {
            callback();
            return true;
        }
        catch (Exception ex)
        {
            LogCompatibilityCallbackException(operation, registration, ex);
            return false;
        }
    }

    private static bool TryInvokeCompatibilityCallback<T>(
        string operation,
        object registration,
        Func<T> callback,
        out T result)
    {
        try
        {
            result = callback();
            return true;
        }
        catch (Exception ex)
        {
            LogCompatibilityCallbackException(operation, registration, ex);
            result = default!;
            return false;
        }
    }

    private static void LogCompatibilityCallbackException(string operation, object registration, Exception ex)
    {
        string category = registration is null
            ? "<unknown>"
            : CompatibilityRegistrationCategory(registration.GetType());
        string key = registration is null ? "<unknown>" : RegistrationKey(registration);
        string message = "Compatibility callback failed during " + operation + ": "
            + ex.GetType().Name + " " + ex.Message;
        AddRegistrationDiagnostic(
            "Error",
            "CompatibilityCallbackException",
            category,
            key,
            message);
    }

    private static string CompatibilityRegistrationCategory(Type type)
    {
        return type.Name;
    }

    private static string RegistrationKey(object registration)
    {
        if (registration is Delegate callback)
        {
            return callback.Method.DeclaringType?.FullName + "." + callback.Method.Name;
        }

        return registration.GetType().FullName ?? registration.GetType().Name;
    }

    private static void ClearCompatibilityRegistrations()
    {
        RaidCaravanMapEntryHandlers.Clear();
        RemoteDefenderMapPreparedHandlers.Clear();
        RemoteMapLoadedHandlers.Clear();
        RemoteMapProjectionReferenceRewriters.Clear();
        RemoteMapProjectionSanitizers.Clear();
        RemoteSessionPawnCleanupHandlers.Clear();
        RemoteMapIntegerLoadIdRewriters.Clear();
        RemoteMapAreaLoadIdProviders.Clear();
        SnapshotSaveSanitizers.Clear();
        PlayerColonySiteRegistrationSuppressors.Clear();
        AdminBaselineExtensionProviders.Clear();
        CompatibilityPackages.Clear();
        WorldConfigurationExtensionHandlers.Clear();
        WorldConfigurationExtensionSummaryProviders.Clear();
        WorldGenerationFloatSettingProviders.Clear();
        FactionPreparedHandlers.Clear();
        PlayerProxyFactionPreparedHandlers.Clear();
        PawnReferenceMetadataCollectors.Clear();
        PawnReferenceMetadataResolvers.Clear();
        PawnReferenceMetadataRestorers.Clear();
        PawnExchangeExtensionAppenders.Clear();
        PawnExchangePackageRegistrars.Clear();
        PawnExchangePackageNormalizers.Clear();
        PawnExchangeLocalSaveOnlyLoadIdPredicates.Clear();
        PawnPostRestoreLocalizers.Clear();
        PawnSoldEffectHandlers.Clear();
        TradeablePawnPredicates.Clear();
        TradePawnRestoreValidators.Clear();
        ThingReferenceEditorRegistrations.Clear();
        ThingReferenceDefaultMetadataProviders.Clear();
        ThingReferenceUniqueRequestPredicates.Clear();
        ThingReferenceMetadataRegistrations.Clear();
        ThingReferenceDisplayRegistrations.Clear();
        ThingReferenceCacheKeyPartsProviders.Clear();
        ThingReferenceMetadataNormalizers.Clear();
        ThingReferenceThingFactories.Clear();
        ThingReferenceDefKindPredicates.Clear();
        DefensePointDefNameProviders.Clear();
        RegistrationRecords.Clear();
        RegistrationDiagnostics.Clear();
        RegistrationPriorities.Clear();
        RegistrationOrders.Clear();
        registrationOrderSequence = 0;
        InvalidateCompatibilityRegistrationSnapshots();
    }

    private static IReadOnlyList<ThingReferenceDisplayRegistration> ThingReferenceDisplayRegistrationSnapshot()
    {
        return cachedThingReferenceDisplayRegistrations ??= ThingReferenceDisplayRegistrations.ToArray();
    }

    private static IReadOnlyList<ThingReferenceCacheKeyPartsProvider> ThingReferenceCacheKeyPartsProviderSnapshot()
    {
        return cachedThingReferenceCacheKeyPartsProviders ??= ThingReferenceCacheKeyPartsProviders.ToArray();
    }

    private static void InvalidateCompatibilityRegistrationSnapshots()
    {
        cachedThingReferenceDisplayRegistrations = null;
        cachedThingReferenceCacheKeyPartsProviders = null;
    }

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
