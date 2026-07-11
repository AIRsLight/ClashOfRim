using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class IdeologyCompatibilityPackage
{
    public static BuiltInDlcCompatibilityPackageDescriptor Descriptor { get; } =
        new(IdeologyCompatibilityKeys.PackageId, _ => Apply(), order: 200);

    private static bool Active => ModLister.IdeologyInstalled && ModsConfig.IdeologyActive;

    private static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            IdeologyCompatibilityKeys.PackageId,
            () => Active,
            new[]
            {
                IdeologyCompatibilityKeys.PawnReference,
                IdeologyCompatibilityKeys.WorldIdeoCatalog
            });

        if (!Active)
        {
            return;
        }

        ClashOfRimCompatibilityApi.RegisterWorldConfigurationExtensionHandler(
            IdeologyCompatibilityKeys.PackageId,
            IdeologyCompatibilityKeys.WorldIdeoCatalog,
            IdeologyWorldConfigurationCompatibility.CollectWorldIdeoCatalogExtension,
            IdeologyWorldConfigurationCompatibility.ApplyWorldIdeoCatalogExtension);
        ClashOfRimCompatibilityApi.RegisterWorldConfigurationExtensionSummaryProvider(IdeologyWorldConfigurationCompatibility.WorldIdeoCatalogSummary);
        ClashOfRimCompatibilityApi.RegisterFactionPreparedHandler(IdeologyPawnReferenceCompatibility.PrepareFactionIdeology);
        ClashOfRimCompatibilityApi.RegisterPlayerProxyFactionPreparedHandler(IdeologyPawnReferenceCompatibility.PreparePlayerProxyFaction);
        ClashOfRimCompatibilityApi.RegisterPawnReferenceMetadataCollector(IdeologyPawnReferenceCompatibility.CollectPawnReferenceMetadata);
        ClashOfRimCompatibilityApi.RegisterPawnReferenceMetadataResolver(
            IdeologyPawnReferenceCompatibility.PawnMetadataIdeoGlobalId,
            IdeologyPawnReferenceCompatibility.ResolvePawnReferenceMetadata);
        ClashOfRimCompatibilityApi.RegisterPawnReferenceMetadataRestorer(
            IdeologyPawnReferenceCompatibility.PawnMetadataIdeoGlobalId,
            IdeologyPawnReferenceCompatibility.RestorePawnReferenceMetadata);
        ClashOfRimCompatibilityApi.RegisterPawnExchangeExtensionAppender(IdeologyPawnReferenceCompatibility.AppendIdeologyPawnExchangeExtension);
        ClashOfRimCompatibilityApi.RegisterPawnExchangePackageNormalizer(IdeologyPawnReferenceCompatibility.NormalizeIdeologyPawnExchangePackage);
        ClashOfRimCompatibilityApi.RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(IdeologyPawnReferenceCompatibility.IsIdeologyLocalSaveOnlyLoadId);
        ClashOfRimCompatibilityApi.RegisterPawnSoldEffectHandler(IdeologyPawnReferenceCompatibility.ApplyPawnSoldEffects);
        ClashOfRimCompatibilityApi.RegisterTradeablePawnPredicate(IdeologyPawnReferenceCompatibility.IsTradeableSlavePawn);
        ClashOfRimCompatibilityApi.RegisterTradePawnRestoreValidator(IdeologyPawnReferenceCompatibility.IsRestorableTradeSlavePawn);
        IdeologyThingTransferCompatibility.Apply();
        ClashOfRimCompatibilityApi.RegisterRemoteMapProjectionReferenceRewriter(IdeologyRemoteMapProjectionRewriter.RewriteRemoteIdeoReferences);
    }
}
