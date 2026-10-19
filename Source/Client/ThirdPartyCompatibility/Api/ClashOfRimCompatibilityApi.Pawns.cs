using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using System.Collections.Generic;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    public static void RegisterFactionPreparedHandler(FactionPreparedHandler handler) =>
        FactionPreparation.RegisterFactionPreparedHandler(handler);

    public static void NotifyFactionPrepared(Faction faction, string purpose, string? ownerUserId = null) =>
        FactionPreparation.NotifyFactionPrepared(faction, purpose, ownerUserId);

    public static void RegisterPlayerProxyFactionPreparedHandler(PlayerProxyFactionPreparedHandler handler) =>
        FactionPreparation.RegisterPlayerProxyFactionPreparedHandler(handler);

    public static void NotifyPlayerProxyFactionPrepared(Faction faction, string ownerUserId) =>
        FactionPreparation.NotifyPlayerProxyFactionPrepared(faction, ownerUserId);

    public static void RegisterPawnReferenceMetadataResolver(string metadataKey, PawnReferenceMetadataResolver resolver) =>
        PawnReferences.RegisterPawnReferenceMetadataResolver(metadataKey, resolver);

    public static void RegisterPawnReferenceMetadataCollector(PawnReferenceMetadataCollector collector) =>
        PawnReferences.RegisterPawnReferenceMetadataCollector(collector);

    public static void CollectPawnReferenceMetadata(
        Pawn pawn,
        Dictionary<string, string?> metadata,
        string? userId,
        string? colonyId) =>
        PawnReferences.CollectPawnReferenceMetadata(pawn, metadata, userId, colonyId);

    public static string? ResolvePawnReferenceMetadata(Pawn pawn, string metadataKey, string? userId, string? colonyId) =>
        PawnReferences.ResolvePawnReferenceMetadata(pawn, metadataKey, userId, colonyId);

    public static void RegisterPawnReferenceMetadataRestorer(string metadataKey, PawnReferenceMetadataRestorer restorer) =>
        PawnReferences.RegisterPawnReferenceMetadataRestorer(metadataKey, restorer);

    public static string RestorePawnReferenceMetadata(Pawn pawn, string metadataKey, string? metadataValue, string label) =>
        PawnReferences.RestorePawnReferenceMetadata(pawn, metadataKey, metadataValue, label);

    public static string RestorePawnReferenceMetadata(
        Pawn pawn,
        IReadOnlyDictionary<string, string?>? metadata,
        string label) =>
        PawnReferences.RestorePawnReferenceMetadata(pawn, metadata, label);

    public static void RegisterPawnExchangeExtensionAppender(PawnExchangeExtensionAppender appender) =>
        PawnExchange.RegisterPawnExchangeExtensionAppender(appender);

    public static void AppendPawnExchangeExtensions(Pawn pawn, ModPawnExchangePackageDto package) =>
        PawnExchange.AppendPawnExchangeExtensions(pawn, package);

    public static void RegisterPawnExchangePackageRegistrar(PawnExchangePackageRegistrar registrar) =>
        PawnExchange.RegisterPawnExchangePackageRegistrar(registrar);

    public static void RegisterPawnExchangePackageNormalizer(PawnExchangePackageNormalizer normalizer) =>
        PawnExchange.RegisterPawnExchangePackageNormalizer(normalizer);

    public static void RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(PawnExchangeLocalSaveOnlyLoadIdPredicate predicate) =>
        PawnExchange.RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(predicate);

    public static void NormalizePawnExchangePackage(ModPawnExchangePackageDto package) =>
        PawnExchange.NormalizePawnExchangePackage(package);

    public static bool TryRegisterPawnExchangePackageExtensions(ModPawnExchangePackageDto package, out string message) =>
        PawnExchange.TryRegisterPawnExchangePackageExtensions(package, out message);

    public static void RegisterPawnPreviewRestorer(PawnPreviewRestorer restorer) => PawnExchange.RegisterPawnPreviewRestorer(restorer);

    internal static bool TryRestorePawnPreview(Pawn pawn, ModPawnExchangePackageDto package, out string message) =>
        PawnExchange.TryRestorePawnPreview(pawn, package, out message);

    public static bool IsPawnExchangeLocalSaveOnlyLoadId(string? loadId) =>
        PawnExchange.IsPawnExchangeLocalSaveOnlyLoadId(loadId);

    public static void RegisterPawnPostRestoreLocalizer(PawnPostRestoreLocalizer localizer) =>
        PawnExchange.RegisterPawnPostRestoreLocalizer(localizer);

    public static void LocalizeRestoredPawnWithCompatibility(Pawn pawn) =>
        PawnExchange.LocalizeRestoredPawnWithCompatibility(pawn);

    public static void RegisterPawnSoldEffectHandler(PawnSoldEffectHandler handler) =>
        PawnTrade.RegisterPawnSoldEffectHandler(handler);

    public static void ApplyPawnSoldEffects(Pawn pawn, Pawn? negotiator) =>
        PawnTrade.ApplyPawnSoldEffects(pawn, negotiator);

    public static void RegisterTradeablePawnPredicate(TradeablePawnPredicate predicate) =>
        PawnTrade.RegisterTradeablePawnPredicate(predicate);

    public static bool IsTradeablePawnByCompatibility(Pawn pawn) =>
        PawnTrade.IsTradeablePawnByCompatibility(pawn);

    public static void RegisterTradePawnRestoreValidator(TradePawnRestoreValidator validator) =>
        PawnTrade.RegisterTradePawnRestoreValidator(validator);

    public static bool IsTradePawnRestoreAllowedByCompatibility(Pawn pawn) =>
        PawnTrade.IsTradePawnRestoreAllowedByCompatibility(pawn);
}
