using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    public static void RegisterFactionPreparedHandler(FactionPreparedHandler handler)
    {
        RegisterUnique(FactionPreparedHandlers, handler);
    }

    public static void NotifyFactionPrepared(Faction faction, string purpose, string? ownerUserId = null)
    {
        if (faction is null)
        {
            return;
        }

        string normalizedPurpose = string.IsNullOrWhiteSpace(purpose) ? "Unknown" : purpose.Trim();
        foreach (FactionPreparedHandler handler in FactionPreparedHandlers.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(NotifyFactionPrepared),
                handler,
                () => handler(faction, normalizedPurpose, ownerUserId));
        }
    }

    public static void RegisterPlayerProxyFactionPreparedHandler(PlayerProxyFactionPreparedHandler handler)
    {
        RegisterUnique(PlayerProxyFactionPreparedHandlers, handler);
    }

    public static void NotifyPlayerProxyFactionPrepared(Faction faction, string ownerUserId)
    {
        if (faction is null || string.IsNullOrWhiteSpace(ownerUserId))
        {
            return;
        }

        NotifyFactionPrepared(faction, "PlayerProxy", ownerUserId);
        foreach (PlayerProxyFactionPreparedHandler handler in PlayerProxyFactionPreparedHandlers.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(NotifyPlayerProxyFactionPrepared),
                handler,
                () => handler(faction, ownerUserId));
        }
    }

    public static void RegisterPawnReferenceMetadataResolver(string metadataKey, PawnReferenceMetadataResolver resolver)
    {
        if (resolver is null)
        {
            return;
        }

        RegisterPawnReferenceMetadataResolverRegistration(
            new PawnReferenceMetadataResolverRegistration(metadataKey, resolver));
    }

    private static void RegisterPawnReferenceMetadataResolverRegistration(
        PawnReferenceMetadataResolverRegistration registration)
    {
        RegisterMetadataKeyedRegistration(
            PawnReferenceMetadataResolvers,
            registration,
            nameof(PawnReferenceMetadataResolverRegistration),
            registration.MetadataKey,
            "PawnReferenceMetadataResolverDuplicateRejected",
            "PawnReferenceMetadataResolverDuplicateReplaced");
    }

    public static void RegisterPawnReferenceMetadataCollector(PawnReferenceMetadataCollector collector)
    {
        RegisterUnique(PawnReferenceMetadataCollectors, collector);
    }

    public static void CollectPawnReferenceMetadata(
        Pawn pawn,
        Dictionary<string, string?> metadata,
        string? userId,
        string? colonyId)
    {
        if (pawn is null || metadata is null)
        {
            return;
        }

        foreach (PawnReferenceMetadataCollector collector in PawnReferenceMetadataCollectors.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(CollectPawnReferenceMetadata),
                collector,
                () => collector(pawn, metadata, userId, colonyId));
        }
    }

    public static string? ResolvePawnReferenceMetadata(Pawn pawn, string metadataKey, string? userId, string? colonyId)
    {
        if (string.IsNullOrWhiteSpace(metadataKey))
        {
            return null;
        }

        foreach (PawnReferenceMetadataResolverRegistration registration in PawnReferenceMetadataResolvers.ToList())
        {
            if (!string.Equals(registration.MetadataKey, metadataKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryInvokeCompatibilityCallback(
                    nameof(ResolvePawnReferenceMetadata),
                    registration,
                    () => registration.Resolver(pawn, metadataKey, userId, colonyId),
                    out string? result))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return null;
    }

    public static void RegisterPawnReferenceMetadataRestorer(string metadataKey, PawnReferenceMetadataRestorer restorer)
    {
        if (restorer is null)
        {
            return;
        }

        RegisterPawnReferenceMetadataRestorerRegistration(
            new PawnReferenceMetadataRestorerRegistration(metadataKey, restorer));
    }

    private static void RegisterPawnReferenceMetadataRestorerRegistration(
        PawnReferenceMetadataRestorerRegistration registration)
    {
        RegisterMetadataKeyedRegistration(
            PawnReferenceMetadataRestorers,
            registration,
            nameof(PawnReferenceMetadataRestorerRegistration),
            registration.MetadataKey,
            "PawnReferenceMetadataRestorerDuplicateRejected",
            "PawnReferenceMetadataRestorerDuplicateReplaced");
    }

    public static string RestorePawnReferenceMetadata(Pawn pawn, string metadataKey, string? metadataValue, string label)
    {
        if (string.IsNullOrWhiteSpace(metadataKey))
        {
            return string.Empty;
        }

        foreach (PawnReferenceMetadataRestorerRegistration registration in PawnReferenceMetadataRestorers.ToList())
        {
            if (!string.Equals(registration.MetadataKey, metadataKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryInvokeCompatibilityCallback(
                    nameof(RestorePawnReferenceMetadata),
                    registration,
                    () => registration.Restorer(pawn, metadataKey, metadataValue, label),
                    out string result))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return string.Empty;
    }

    public static string RestorePawnReferenceMetadata(
        Pawn pawn,
        IReadOnlyDictionary<string, string?>? metadata,
        string label)
    {
        if (pawn is null || metadata is null || metadata.Count == 0)
        {
            return string.Empty;
        }

        List<string> messages = new();
        foreach (KeyValuePair<string, string?> entry in metadata)
        {
            string message = RestorePawnReferenceMetadata(pawn, entry.Key, entry.Value, label);
            if (!string.IsNullOrWhiteSpace(message))
            {
                messages.Add(message);
            }
        }

        return string.Concat(messages);
    }

    public static void RegisterPawnExchangeExtensionAppender(PawnExchangeExtensionAppender appender)
    {
        RegisterUnique(PawnExchangeExtensionAppenders, appender);
    }

    public static void AppendPawnExchangeExtensions(Pawn pawn, ModPawnExchangePackageDto package)
    {
        if (pawn is null || package is null)
        {
            return;
        }

        foreach (PawnExchangeExtensionAppender appender in PawnExchangeExtensionAppenders.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(AppendPawnExchangeExtensions),
                appender,
                () => appender(pawn, package));
        }
    }

    public static void RegisterPawnExchangePackageRegistrar(PawnExchangePackageRegistrar registrar)
    {
        RegisterUnique(PawnExchangePackageRegistrars, registrar);
    }

    public static void RegisterPawnExchangePackageNormalizer(PawnExchangePackageNormalizer normalizer)
    {
        RegisterUnique(PawnExchangePackageNormalizers, normalizer);
    }

    public static void RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(PawnExchangeLocalSaveOnlyLoadIdPredicate predicate)
    {
        RegisterUnique(PawnExchangeLocalSaveOnlyLoadIdPredicates, predicate);
    }

    public static void NormalizePawnExchangePackage(ModPawnExchangePackageDto package)
    {
        if (package is null)
        {
            return;
        }

        foreach (PawnExchangePackageNormalizer normalizer in PawnExchangePackageNormalizers.ToList())
        {
            string key = RegistrationKey(normalizer);
            ClashLog.Message("[ClashOfRim][Compatibility] pawn package normalizer begin: " + key + ".");
            TryInvokeCompatibilityCallback(
                nameof(NormalizePawnExchangePackage),
                normalizer,
                () => normalizer(package));
            ClashLog.Message("[ClashOfRim][Compatibility] pawn package normalizer end: " + key + ".");
        }
    }

    public static bool TryRegisterPawnExchangePackageExtensions(ModPawnExchangePackageDto package, out string message)
    {
        message = string.Empty;
        if (package is null)
        {
            return true;
        }

        foreach (PawnExchangePackageRegistrar registrar in PawnExchangePackageRegistrars.ToList())
        {
            string registrarMessage = string.Empty;
            if (!TryInvokeCompatibilityCallback(
                    nameof(TryRegisterPawnExchangePackageExtensions),
                    registrar,
                    () => registrar(package, out registrarMessage),
                    out bool accepted))
            {
                message = "Compatibility package failed.";
                return false;
            }

            message = registrarMessage;
            if (!accepted)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsPawnExchangeLocalSaveOnlyLoadId(string? loadId)
    {
        if (string.IsNullOrWhiteSpace(loadId))
        {
            return false;
        }

        foreach (PawnExchangeLocalSaveOnlyLoadIdPredicate predicate in PawnExchangeLocalSaveOnlyLoadIdPredicates.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(IsPawnExchangeLocalSaveOnlyLoadId),
                    predicate,
                    () => predicate(loadId!),
                    out bool matched))
            {
                continue;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    public static void RegisterPawnPostRestoreLocalizer(PawnPostRestoreLocalizer localizer)
    {
        RegisterUnique(PawnPostRestoreLocalizers, localizer);
    }

    public static void LocalizeRestoredPawnWithCompatibility(Pawn pawn)
    {
        foreach (PawnPostRestoreLocalizer localizer in PawnPostRestoreLocalizers.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(LocalizeRestoredPawnWithCompatibility),
                localizer,
                () => localizer(pawn));
        }
    }

    public static void RegisterPawnSoldEffectHandler(PawnSoldEffectHandler handler)
    {
        RegisterUnique(PawnSoldEffectHandlers, handler);
    }

    public static void ApplyPawnSoldEffects(Pawn pawn, Pawn? negotiator)
    {
        foreach (PawnSoldEffectHandler handler in PawnSoldEffectHandlers.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(ApplyPawnSoldEffects),
                handler,
                () => handler(pawn, negotiator));
        }
    }

    public static void RegisterTradeablePawnPredicate(TradeablePawnPredicate predicate)
    {
        RegisterUnique(TradeablePawnPredicates, predicate);
    }

    public static bool IsTradeablePawnByCompatibility(Pawn pawn)
    {
        if (pawn is null)
        {
            return false;
        }

        foreach (TradeablePawnPredicate predicate in TradeablePawnPredicates.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(IsTradeablePawnByCompatibility),
                    predicate,
                    () => predicate(pawn),
                    out bool matched))
            {
                continue;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    public static void RegisterTradePawnRestoreValidator(TradePawnRestoreValidator validator)
    {
        RegisterUnique(TradePawnRestoreValidators, validator);
    }

    public static bool IsTradePawnRestoreAllowedByCompatibility(Pawn pawn)
    {
        if (pawn is null)
        {
            return false;
        }

        foreach (TradePawnRestoreValidator validator in TradePawnRestoreValidators.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(IsTradePawnRestoreAllowedByCompatibility),
                    validator,
                    () => validator(pawn),
                    out bool matched))
            {
                continue;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
