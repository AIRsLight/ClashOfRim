using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public delegate RaidCaravanMapEntryResult RaidCaravanMapEntryHandler(
    Caravan caravan,
    Map map,
    IReadOnlyList<Pawn> attackPawns);

public delegate void RemoteDefenderMapPreparedHandler(Map map, Faction defenderFaction);

public delegate void RemoteMapLoadedHandler(
    Map map,
    RemoteSessionMapParent carrier,
    string scope,
    ModSnapshotPackageMetadataDto package);

public delegate IReadOnlyList<ILoadReferenceable> RemoteMapProjectionReferenceRewriter(
    ModSnapshotPackageMetadataDto package,
    XDocument sourceDocument,
    XElement projectionElement);

public delegate void RemoteMapProjectionSanitizer(
    ModSnapshotPackageMetadataDto package,
    XElement mapElement,
    XElement referencePawnsElement);

public delegate bool RemoteSessionPawnCleanupHandler(Pawn pawn, string reason);

public delegate string? RemoteMapAreaLoadIdProvider(XElement area, int id);

public delegate int CompatibilitySnapshotSaveSanitizer(XDocument document);

public delegate bool PlayerColonySiteRegistrationSuppressor();

public delegate ModAdminBaselineExtensionDto? AdminBaselineExtensionProvider();

public delegate IReadOnlyList<ModWorldConfigurationExtensionDto> WorldConfigurationExtensionCollector(
    string? userId,
    string? colonyId,
    string worldConfigurationId);

public delegate int WorldConfigurationExtensionApplier(
    ModWorldConfigurationDto configuration,
    string? localUserId,
    bool applyWorldState);

internal sealed class WorldConfigurationExtensionHandlerRegistration
{
    public WorldConfigurationExtensionHandlerRegistration(
        string providerId,
        string kind,
        WorldConfigurationExtensionCollector? collector,
        WorldConfigurationExtensionApplier? applier)
    {
        ProviderId = providerId.Trim();
        Kind = kind.Trim();
        Collector = collector;
        Applier = applier;
        RequestKey = Key(ProviderId, Kind);
    }

    public string ProviderId { get; }

    public string Kind { get; }

    public WorldConfigurationExtensionCollector? Collector { get; }

    public WorldConfigurationExtensionApplier? Applier { get; }

    public string RequestKey { get; }

    public static string Key(string providerId, string kind)
    {
        return (providerId ?? string.Empty).Trim() + "\u001f" + (kind ?? string.Empty).Trim();
    }
}

public sealed class WorldConfigurationExtensionSummaryItem
{
    public WorldConfigurationExtensionSummaryItem(string key, string label, string value)
    {
        Key = key ?? string.Empty;
        Label = label ?? string.Empty;
        Value = value ?? string.Empty;
    }

    public string Key { get; }

    public string Label { get; }

    public string Value { get; }
}

public delegate IReadOnlyList<WorldConfigurationExtensionSummaryItem> WorldConfigurationExtensionSummaryProvider(
    ModWorldConfigurationDto configuration);

internal sealed class WorldGenerationFloatSettingProviderRegistration
{
    public WorldGenerationFloatSettingProviderRegistration(
        string settingKey,
        WorldGenerationFloatSettingProvider provider)
    {
        SettingKey = settingKey ?? string.Empty;
        Provider = provider;
    }

    public string SettingKey { get; }

    public WorldGenerationFloatSettingProvider Provider { get; }
}

public delegate bool WorldGenerationFloatSettingProvider(ModWorldConfigurationDto configuration, out float value);

public delegate void FactionPreparedHandler(Faction faction, string purpose, string? ownerUserId);

public delegate void PlayerProxyFactionPreparedHandler(Faction faction, string ownerUserId);

public delegate void PawnReferenceMetadataCollector(
    Pawn pawn,
    Dictionary<string, string?> metadata,
    string? userId,
    string? colonyId);

public delegate string? PawnReferenceMetadataResolver(Pawn pawn, string metadataKey, string? userId, string? colonyId);

internal sealed class PawnReferenceMetadataResolverRegistration : IMetadataKeyedRegistration
{
    public PawnReferenceMetadataResolverRegistration(string metadataKey, PawnReferenceMetadataResolver resolver)
    {
        MetadataKey = metadataKey?.Trim() ?? string.Empty;
        Resolver = resolver;
    }

    public string MetadataKey { get; }

    public PawnReferenceMetadataResolver Resolver { get; }
}

public delegate string PawnReferenceMetadataRestorer(Pawn pawn, string metadataKey, string? metadataValue, string label);

internal sealed class PawnReferenceMetadataRestorerRegistration : IMetadataKeyedRegistration
{
    public PawnReferenceMetadataRestorerRegistration(string metadataKey, PawnReferenceMetadataRestorer restorer)
    {
        MetadataKey = metadataKey?.Trim() ?? string.Empty;
        Restorer = restorer;
    }

    public string MetadataKey { get; }

    public PawnReferenceMetadataRestorer Restorer { get; }
}

public delegate void PawnExchangeExtensionAppender(Pawn pawn, ModPawnExchangePackageDto package);

internal interface IMetadataKeyedRegistration
{
    string MetadataKey { get; }
}

public delegate bool PawnExchangePackageRegistrar(ModPawnExchangePackageDto package, out string message);

public delegate void PawnExchangePackageNormalizer(ModPawnExchangePackageDto package);

public delegate bool PawnExchangeLocalSaveOnlyLoadIdPredicate(string loadId);

public delegate void PawnPostRestoreLocalizer(Pawn pawn);

public delegate void PawnSoldEffectHandler(Pawn pawn, Pawn? negotiator);

public delegate bool TradeablePawnPredicate(Pawn pawn);

public delegate bool TradePawnRestoreValidator(Pawn pawn);

public delegate bool ThingReferenceEditorHandler(
    string surface,
    ThingDef? def,
    ModThingReferenceDto item,
    Rect rect,
    out float consumedHeight);

public delegate void ThingReferenceMetadataCleaner(string surface, ThingDef? def, ModThingReferenceDto item);

public delegate bool ThingReferenceCompletenessValidator(string surface, ThingDef? def, ModThingReferenceDto item);

internal sealed class ThingReferenceEditorRegistration : IMetadataKeyedRegistration
{
    public ThingReferenceEditorRegistration(
        string metadataKey,
        ThingReferenceEditorHandler editor,
        ThingReferenceMetadataCleaner cleaner,
        ThingReferenceCompletenessValidator validator)
    {
        MetadataKey = metadataKey?.Trim() ?? string.Empty;
        Editor = editor;
        Cleaner = cleaner;
        Validator = validator;
    }

    public string MetadataKey { get; }

    public ThingReferenceEditorHandler Editor { get; }

    public ThingReferenceMetadataCleaner Cleaner { get; }

    public ThingReferenceCompletenessValidator Validator { get; }
}

public delegate void ThingReferenceDefaultMetadataProvider(string surface, ThingDef? def, ModThingReferenceDto item);

public delegate bool ThingReferenceUniqueRequestPredicate(ThingDef? def);

public delegate void ThingReferenceMetadataAppender(Thing metadataThing, ModThingReferenceDto reference);

public delegate bool ThingReferenceThingMatcher(ModThingReferenceDto requirement, Thing metadataThing);

public delegate bool ThingReferenceDtoMatcher(ModThingReferenceDto requirement, ModThingReferenceDto candidate);

public delegate bool ThingReferenceMetadataApplier(ModThingReferenceDto reference, Thing thing, out string? missingDefName);

public delegate int ThingReferenceStrictnessProvider(ModThingReferenceDto requirement);

internal sealed class ThingReferenceMetadataRegistration : IMetadataKeyedRegistration
{
    public ThingReferenceMetadataRegistration(
        string metadataKey,
        ThingReferenceMetadataAppender appender,
        ThingReferenceThingMatcher thingMatcher,
        ThingReferenceDtoMatcher dtoMatcher,
        ThingReferenceMetadataApplier applier,
        ThingReferenceStrictnessProvider strictnessProvider)
    {
        MetadataKey = metadataKey?.Trim() ?? string.Empty;
        Appender = appender;
        ThingMatcher = thingMatcher;
        DtoMatcher = dtoMatcher;
        Applier = applier;
        StrictnessProvider = strictnessProvider;
    }

    public string MetadataKey { get; }

    public ThingReferenceMetadataAppender Appender { get; }

    public ThingReferenceThingMatcher ThingMatcher { get; }

    public ThingReferenceDtoMatcher DtoMatcher { get; }

    public ThingReferenceMetadataApplier Applier { get; }

    public ThingReferenceStrictnessProvider StrictnessProvider { get; }
}

public delegate void ThingReferenceDisplayPartsProvider(ModThingReferenceDto thing, bool asRequirement, List<string> parts);

public delegate bool ThingReferenceStandardStatSuppressor(ThingDef? def);

internal sealed class ThingReferenceDisplayRegistration : IMetadataKeyedRegistration
{
    public ThingReferenceDisplayRegistration(
        string metadataKey,
        ThingReferenceDisplayPartsProvider partsProvider,
        ThingReferenceStandardStatSuppressor standardStatSuppressor)
    {
        MetadataKey = metadataKey?.Trim() ?? string.Empty;
        PartsProvider = partsProvider;
        StandardStatSuppressor = standardStatSuppressor;
    }

    public string MetadataKey { get; }

    public ThingReferenceDisplayPartsProvider PartsProvider { get; }

    public ThingReferenceStandardStatSuppressor StandardStatSuppressor { get; }
}

public delegate IEnumerable<string> ThingReferenceCacheKeyPartsProvider(ModThingReferenceDto thing);

public delegate void ThingReferenceMetadataNormalizer(ModThingReferenceDto thing);

public delegate bool ThingReferenceThingFactory(ThingDef def, ThingDef? stuff, out Thing? thing);

internal sealed class ThingReferenceThingFactoryRegistration : IMetadataKeyedRegistration
{
    public ThingReferenceThingFactoryRegistration(string metadataKey, ThingReferenceThingFactory factory)
    {
        MetadataKey = metadataKey?.Trim() ?? string.Empty;
        Factory = factory;
    }

    public string MetadataKey { get; }

    public ThingReferenceThingFactory Factory { get; }
}

public delegate bool ThingReferenceDefKindPredicate(ThingDef? def);

internal sealed class ThingReferenceDefKindPredicateRegistration
{
    public ThingReferenceDefKindPredicateRegistration(string kind, ThingReferenceDefKindPredicate predicate)
    {
        Kind = kind?.Trim() ?? string.Empty;
        Predicate = predicate;
    }

    public string Kind { get; }

    public ThingReferenceDefKindPredicate Predicate { get; }
}

public delegate IEnumerable<string> DefensePointDefNameProvider();


internal sealed class AdminBaselineExtensionProviderRegistration
{
    public AdminBaselineExtensionProviderRegistration(
        string providerId,
        string kind,
        AdminBaselineExtensionProvider provider)
    {
        ProviderId = providerId;
        Kind = kind;
        Provider = provider;
        RequestKey = Key(providerId, kind);
    }

    public string ProviderId { get; }

    public string Kind { get; }

    public AdminBaselineExtensionProvider Provider { get; }

    public string RequestKey { get; }

    public static string Key(string providerId, string kind)
    {
        return providerId.Trim() + "\u001f" + kind.Trim();
    }
}

internal sealed class CompatibilityPackageRegistration
{
    public CompatibilityPackageRegistration(
        string packageId,
        Func<bool> isLocallyActive,
        IReadOnlyCollection<string> capabilities)
    {
        PackageId = packageId;
        IsLocallyActive = isLocallyActive;
        Capabilities = new HashSet<string>(capabilities, StringComparer.OrdinalIgnoreCase);
    }

    public string PackageId { get; }

    public Func<bool> IsLocallyActive { get; }

    public IReadOnlyCollection<string> Capabilities { get; }
}

public sealed class RemoteMapIntegerLoadIdRewriterRegistration
{
    internal RemoteMapIntegerLoadIdRewriterRegistration(
        string loadIdPrefix,
        Func<XElement, bool> predicate,
        Func<int> nextId)
    {
        LoadIdPrefix = loadIdPrefix;
        Predicate = predicate;
        NextId = nextId;
    }

    public string LoadIdPrefix { get; }

    public Func<XElement, bool> Predicate { get; }

    public Func<int> NextId { get; }
}

public sealed class CompatibilityRegistrationToken
{
    internal CompatibilityRegistrationToken(string owner, int generation, int id, int priority)
    {
        Owner = owner;
        Generation = generation;
        Id = id;
        Priority = priority;
        CreatedAtUtc = DateTime.UtcNow;
    }

    internal int Id { get; }

    public string Owner { get; }

    public int Generation { get; }

    public int Priority { get; }

    public DateTime CreatedAtUtc { get; }
}

internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public new bool Equals(object? x, object? y)
    {
        return ReferenceEquals(x, y);
    }

    public int GetHashCode(object obj)
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

public sealed class CompatibilityRegistrationRecord
{
    internal CompatibilityRegistrationRecord(
        string category,
        string key,
        string owner,
        int generation,
        int priority)
    {
        Category = category ?? string.Empty;
        Key = key ?? string.Empty;
        Owner = owner ?? string.Empty;
        Generation = generation;
        Priority = priority;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public string Category { get; }

    public string Key { get; }

    public string Owner { get; }

    public int Generation { get; }

    public int Priority { get; }

    public DateTime CreatedAtUtc { get; }
}

public sealed class CompatibilityRegistrationDiagnostic
{
    internal CompatibilityRegistrationDiagnostic(
        string severity,
        string code,
        string category,
        string key,
        string message)
    {
        Severity = severity ?? string.Empty;
        Code = code ?? string.Empty;
        Category = category ?? string.Empty;
        Key = key ?? string.Empty;
        Message = message ?? string.Empty;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public string Severity { get; }

    public string Code { get; }

    public string Category { get; }

    public string Key { get; }

    public string Message { get; }

    public DateTime CreatedAtUtc { get; }
}

public enum RaidCaravanMapEntryResultKind
{
    NotHandled,
    Success,
    Failed
}

public sealed class RaidCaravanMapEntryResult
{
    private RaidCaravanMapEntryResult(
        RaidCaravanMapEntryResultKind kind,
        IReadOnlyList<string>? attackPawnThingIds,
        string? failureReason)
    {
        Kind = kind;
        AttackPawnThingIds = attackPawnThingIds ?? new List<string>();
        FailureReason = failureReason ?? string.Empty;
    }

    public static RaidCaravanMapEntryResult NotHandled { get; } = new(
        RaidCaravanMapEntryResultKind.NotHandled,
        null,
        null);

    public RaidCaravanMapEntryResultKind Kind { get; }

    public IReadOnlyList<string> AttackPawnThingIds { get; }

    public string FailureReason { get; }

    public static RaidCaravanMapEntryResult Success(IReadOnlyList<string> attackPawnThingIds)
    {
        return new RaidCaravanMapEntryResult(RaidCaravanMapEntryResultKind.Success, attackPawnThingIds, null);
    }

    public static RaidCaravanMapEntryResult Failed(string failureReason)
    {
        return new RaidCaravanMapEntryResult(RaidCaravanMapEntryResultKind.Failed, null, failureReason);
    }
}
