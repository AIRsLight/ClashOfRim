using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Save;

public sealed class SnapshotUploadReceiver
{
    private readonly IColonySnapshotIndexStore store;
    private readonly SnapshotUploadPolicy policy;
    private readonly IReadOnlyList<ISaveIndexExtension>? saveIndexExtensions;
    private readonly IReadOnlyList<Func<WorldObjectSummary, bool>>? additionalPlayerColonyAnchorPredicates;

    public SnapshotUploadReceiver(
        IColonySnapshotIndexStore store,
        SnapshotUploadPolicy? policy = null,
        IReadOnlyList<ISaveIndexExtension>? saveIndexExtensions = null,
        IReadOnlyList<Func<WorldObjectSummary, bool>>? additionalPlayerColonyAnchorPredicates = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.policy = policy ?? SnapshotUploadPolicy.AllowAnyVersion;
        this.saveIndexExtensions = saveIndexExtensions;
        this.additionalPlayerColonyAnchorPredicates = additionalPlayerColonyAnchorPredicates;
    }

    public SnapshotUploadResult Receive(
        SnapshotUploadContext context,
        SaveSnapshotPackage package,
        DateTimeOffset acceptedAtUtc,
        bool storeAccepted = true,
        bool validateGameplayContinuity = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(package);

        SaveSnapshotEnvelope envelope = package.Envelope;
        SnapshotIdentity identity = envelope.Identity;

        SnapshotUploadResult? earlyRejection = ValidateEnvelope(context, envelope);
        if (earlyRejection is not null)
        {
            return earlyRejection;
        }

        if (!HashMatches(package.Payload, envelope.PayloadSha256))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.PayloadHashMismatch,
                "Uploaded payload SHA-256 does not match the package envelope.");
        }

        byte[] originalPayload;
        try
        {
            originalPayload = DecodePayload(package.Payload, envelope.PayloadEncoding);
        }
        catch (NotSupportedException)
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.UnsupportedPayloadEncoding,
                "Snapshot payload encoding is not supported.");
        }
        catch (InvalidDataException ex)
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.InvalidPayload,
                $"Snapshot payload could not be decoded: {ex.Message}");
        }

        if (originalPayload.LongLength != envelope.OriginalSaveBytes)
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.InvalidPayload,
                "Original save size does not match the package envelope.");
        }

        if (!HashMatches(originalPayload, envelope.OriginalSha256))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.OriginalHashMismatch,
                "Original save SHA-256 does not match the package envelope.");
        }

        SaveSnapshotEnvelope payloadEnvelope = ApplyPayloadMetadata(envelope, originalPayload);
        SaveSnapshotIndex rebuiltIndex;
        try
        {
            rebuiltIndex = RebuildIndex(originalPayload, identity);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException)
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.InvalidPayload,
                $"Snapshot payload index could not be rebuilt: {ex.Message}");
        }

        if (!string.Equals(rebuiltIndex.Meta.GameVersion, payloadEnvelope.RimWorldVersion, StringComparison.Ordinal))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.InvalidPayload,
                "RimWorld version in the package envelope does not match the save content.");
        }

        if (string.IsNullOrWhiteSpace(rebuiltIndex.Meta.GameVersion))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.MissingRimWorldVersion,
                "Save content is missing the RimWorld version.");
        }

        if (!policy.IsVersionAllowed(rebuiltIndex.Meta.GameVersion))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.IncompatibleRimWorldVersion,
                "RimWorld version is outside the server allowed range.");
        }

        LatestSnapshotRecord? previous = GetPreviousSnapshot(identity);
        SnapshotUploadResult? replayRejection = ValidateSnapshotReplay(previous, payloadEnvelope);
        if (replayRejection is not null)
        {
            return replayRejection;
        }

        SnapshotUploadResult? lineageRejection = ValidateSnapshotLineage(previous, payloadEnvelope);
        if (lineageRejection is not null)
        {
            return lineageRejection;
        }

        SnapshotUploadValidationRules validationRules = context.ValidationRules;
        SnapshotUploadResult? uploadKindRejection = ValidateSnapshotUploadKindSpecificRules(
            validationRules,
            rebuiltIndex);
        if (uploadKindRejection is not null)
        {
            return uploadKindRejection;
        }

        if (validateGameplayContinuity)
        {
            if (validationRules.ValidateSnapshotTime)
            {
                SnapshotUploadResult? timeRejection = ValidateSnapshotTime(previous, payloadEnvelope);
                if (timeRejection is not null)
                {
                    return timeRejection;
                }
            }

            if (validationRules.ValidateColonyContinuity)
            {
                SnapshotUploadResult? continuityRejection = ValidateSnapshotContinuity(
                    validationRules,
                    previous,
                    rebuiltIndex);
                if (continuityRejection is not null)
                {
                    return continuityRejection;
                }
            }
        }

        SaveSnapshotEnvelope acceptedEnvelope = payloadEnvelope with
        {
            NextLineageToken = GenerateLineageToken()
        };
        var acceptedPackage = package with
        {
            Envelope = acceptedEnvelope
        };
        var snapshot = new LatestSnapshotRecord(identity, acceptedEnvelope, rebuiltIndex, acceptedAtUtc);
        if (storeAccepted)
        {
            if (store is IColonySnapshotPackageStore packageStore)
            {
                packageStore.StoreLatest(acceptedPackage, rebuiltIndex, acceptedAtUtc);
            }
            else
            {
                store.StoreLatest(snapshot);
            }
        }

        return SnapshotUploadResult.Accept(snapshot, context.SnapshotUploadKind);
    }

    private static SaveSnapshotEnvelope ApplyPayloadMetadata(SaveSnapshotEnvelope envelope, byte[] originalPayload)
    {
        PayloadSnapshotMetadata metadata = ReadPayloadSnapshotMetadata(originalPayload);
        return envelope with
        {
            PreviousSnapshotId = metadata.LineageSnapshotId,
            LineageToken = metadata.LineageToken,
            GameTicks = metadata.GameTicks
        };
    }

    private static PayloadSnapshotMetadata ReadPayloadSnapshotMetadata(byte[] originalPayload)
    {
        using var stream = new MemoryStream(originalPayload);
        XDocument document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        XElement? game = document.Root?.Element("game");
        long? gameTicks = long.TryParse(
            game?.Element("tickManager")?.Element("ticksGame")?.Value.Trim(),
            out long ticks)
            ? ticks
            : null;
        XElement? component = game?
            .Element("components")?
            .Elements("li")
            .FirstOrDefault(item => IsClashOfRimGameComponentClass(item.Attribute("Class")?.Value));
        string? lineageSnapshotId = component?.Element("clashOfRimLineageSnapshotId")?.Value.Trim();
        string? lineageToken = component?.Element("clashOfRimLineageToken")?.Value.Trim();

        return new PayloadSnapshotMetadata(
            string.IsNullOrWhiteSpace(lineageSnapshotId) ? null : lineageSnapshotId,
            string.IsNullOrWhiteSpace(lineageToken) ? null : lineageToken,
            gameTicks);
    }

    private static bool IsClashOfRimGameComponentClass(string? className)
    {
        return !string.IsNullOrWhiteSpace(className)
            && className.Contains("AIRsLight.ClashOfRim.ClashOfRimGameComponent", StringComparison.Ordinal);
    }

    private LatestSnapshotRecord? GetPreviousSnapshot(SnapshotIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.OwnerId) || string.IsNullOrWhiteSpace(identity.ColonyId))
        {
            return null;
        }

        return store.GetLatest(identity.OwnerId!, identity.ColonyId!);
    }

    private SnapshotUploadResult? ValidateSnapshotReplay(
        LatestSnapshotRecord? previous,
        SaveSnapshotEnvelope envelope)
    {
        SnapshotIdentity identity = envelope.Identity;
        if (previous is null
            || string.IsNullOrWhiteSpace(identity.OwnerId)
            || string.IsNullOrWhiteSpace(identity.ColonyId)
            || string.IsNullOrWhiteSpace(envelope.OriginalSha256)
            || store is not IColonySnapshotHistoryStore historyStore)
        {
            return null;
        }

        string? expectedToken = previous.Envelope.NextLineageToken;
        if (!string.IsNullOrWhiteSpace(expectedToken)
            && string.Equals(envelope.LineageToken, expectedToken, StringComparison.Ordinal))
        {
            return null;
        }

        if (historyStore.HasAcceptedOriginalHash(identity.OwnerId!, identity.ColonyId!, envelope.OriginalSha256)
            && !string.Equals(identity.SnapshotId, previous.Identity.SnapshotId, StringComparison.Ordinal))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.SnapshotReplayDetected,
                "This save content was already submitted as a historical snapshot and cannot be repackaged as a new snapshot.");
        }

        return null;
    }

    private static SnapshotUploadResult? ValidateSnapshotLineage(
        LatestSnapshotRecord? previous,
        SaveSnapshotEnvelope envelope)
    {
        if (previous is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(envelope.PreviousSnapshotId)
            && !string.Equals(envelope.PreviousSnapshotId, previous.Identity.SnapshotId, StringComparison.Ordinal))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.SnapshotLineageMismatch,
                "Snapshot previous id is not the server current snapshot; the save may be old or forked.");
        }

        string? expectedToken = previous.Envelope.NextLineageToken;
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            return null;
        }

        if (!string.Equals(envelope.LineageToken, expectedToken, StringComparison.Ordinal))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.SnapshotLineageMismatch,
                "Snapshot is missing the server current lineage token, or the token has expired; rollback is suspected.");
        }

        return null;
    }

    private static SnapshotUploadResult? ValidateSnapshotUploadKindSpecificRules(
        SnapshotUploadValidationRules validationRules,
        SaveSnapshotIndex rebuiltIndex)
    {
        if (!validationRules.RequiresRaidSettlementBattleMap)
        {
            return null;
        }

        string requiredRaidEventId = validationRules.RequiredRaidEventId!;
        HashSet<string> matchingRaidBattleWorldObjectIds = rebuiltIndex.WorldObjects
            .Where(worldObject => string.Equals(worldObject.Def, "ClashOfRim_RemoteRaidBattleMapParent", StringComparison.Ordinal))
            .Where(worldObject => string.Equals(worldObject.ClashOfRimRelatedEventId, requiredRaidEventId, StringComparison.Ordinal))
            .Select(worldObject => worldObject.UniqueLoadId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
        if (matchingRaidBattleWorldObjectIds.Count == 0)
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.InvalidPayload,
                "Raid settlement evidence snapshot is missing the remote raid battle world object for the requested raid event.");
        }

        bool hasMatchingMap = rebuiltIndex.Maps.Any(map =>
            !string.IsNullOrWhiteSpace(map.ParentWorldObjectId)
            && matchingRaidBattleWorldObjectIds.Contains(map.ParentWorldObjectId!));
        if (!hasMatchingMap)
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.InvalidPayload,
                "Raid settlement evidence snapshot is missing the remote raid battle map for the requested raid event.");
        }

        return null;
    }

    private static SnapshotUploadResult? ValidateSnapshotTime(
        LatestSnapshotRecord? previous,
        SaveSnapshotEnvelope envelope)
    {
        if (previous?.Envelope.GameTicks is null || envelope.GameTicks is null)
        {
            return null;
        }

        if (envelope.GameTicks.Value < previous.Envelope.GameTicks.Value)
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.SnapshotTimeRegression,
                "Snapshot game tick is earlier than the server current snapshot; rollback is suspected.");
        }

        return null;
    }

    private SnapshotUploadResult? ValidateSnapshotContinuity(
        SnapshotUploadValidationRules validationRules,
        LatestSnapshotRecord? previous,
        SaveSnapshotIndex rebuiltIndex)
    {
        if (previous is null)
        {
            return null;
        }

        IReadOnlyList<ColonyAnchor> expectedAnchors = ExtractColonyAnchors(previous.Index);
        if (expectedAnchors.Count == 0)
        {
            return null;
        }

        IReadOnlyList<ColonyAnchor> actualAnchors = ExtractColonyAnchors(rebuiltIndex);
        if (actualAnchors.Any(actual => expectedAnchors.Any(expected => IsSameAnchor(expected, actual))))
        {
            return null;
        }

        if (IsSingleColonyRelocation(validationRules, expectedAnchors, actualAnchors))
        {
            return null;
        }

        return SnapshotUploadResult.Reject(
            SnapshotUploadResultKind.SnapshotContinuityMismatch,
            "Snapshot does not contain the server-registered player colony anchor; unrelated local save upload is suspected.");
    }

    private static bool IsSingleColonyRelocation(
        SnapshotUploadValidationRules validationRules,
        IReadOnlyList<ColonyAnchor> expectedAnchors,
        IReadOnlyList<ColonyAnchor> actualAnchors)
    {
        return validationRules.AllowSingleColonyRelocation
            && expectedAnchors.Count == 1
            && actualAnchors.Count == 1;
    }

    private IReadOnlyList<ColonyAnchor> ExtractColonyAnchors(SaveSnapshotIndex index)
    {
        var anchors = new List<ColonyAnchor>();
        HashSet<string> playerFactionIds = BuildPlayerFactionIds(index);
        foreach (MapSummary map in index.Maps)
        {
            if (string.IsNullOrWhiteSpace(map.ParentWorldObjectId))
            {
                continue;
            }

            WorldObjectSummary? worldObject = FindWorldObjectById(index.WorldObjects, map.ParentWorldObjectId!);
            if (worldObject is null
                || worldObject.Destroyed
                || !IsPlayerColonyAnchor(map, worldObject, playerFactionIds)
                || !TryParseTile(worldObject.Tile, out int tile, out int tileLayerId))
            {
                continue;
            }

            anchors.Add(new ColonyAnchor(
                map.UniqueId,
                worldObject.UniqueLoadId ?? worldObject.Id,
                tile,
                tileLayerId));
        }

        return anchors
            .GroupBy(anchor => anchor.WorldObjectId ?? anchor.MapUniqueId ?? "tile:" + anchor.Tile + "," + anchor.TileLayerId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static HashSet<string> BuildPlayerFactionIds(SaveSnapshotIndex index)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (FactionSummary faction in index.Factions)
        {
            if (!IsPlayerFaction(faction))
            {
                continue;
            }

            AddFactionId(result, faction.UniqueLoadId);
            AddFactionId(result, faction.LoadId);
            if (!string.IsNullOrWhiteSpace(faction.LoadId))
            {
                AddFactionId(result, "Faction_" + faction.LoadId);
            }
        }

        return result;
    }

    private static bool IsPlayerFaction(FactionSummary faction)
    {
        return string.Equals(faction.Def, "PlayerColony", StringComparison.Ordinal)
            || string.Equals(faction.Def, "PlayerTribe", StringComparison.Ordinal);
    }

    private static void AddFactionId(HashSet<string> result, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            result.Add(value);
        }
    }

    private static WorldObjectSummary? FindWorldObjectById(
        IEnumerable<WorldObjectSummary> worldObjects,
        string worldObjectId)
    {
        return worldObjects.FirstOrDefault(candidate =>
            string.Equals(candidate.UniqueLoadId, worldObjectId, StringComparison.Ordinal)
            || string.Equals(candidate.Id, worldObjectId, StringComparison.Ordinal));
    }

    private bool IsPlayerColonyAnchor(
        MapSummary map,
        WorldObjectSummary worldObject,
        IReadOnlySet<string> playerFactionIds)
    {
        if (WorldObjectTypeIdentity.IsSettlement(worldObject))
        {
            return HasPlayerColonyEvidence(map, worldObject, playerFactionIds);
        }

        if (!map.WasSpawnedViaGravshipLanding || additionalPlayerColonyAnchorPredicates is null)
        {
            return false;
        }

        return HasPlayerColonyEvidence(map, worldObject, playerFactionIds)
            && additionalPlayerColonyAnchorPredicates.Any(predicate => predicate(worldObject));
    }

    private static bool HasPlayerColonyEvidence(
        MapSummary map,
        WorldObjectSummary worldObject,
        IReadOnlySet<string> playerFactionIds)
    {
        return map.PlayerColonistCount > 0
            || (!string.IsNullOrWhiteSpace(worldObject.Faction)
                && playerFactionIds.Contains(worldObject.Faction!));
    }

    private static bool TryParseTile(string? value, out int tile, out int tileLayerId)
    {
        tile = default;
        tileLayerId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split(',', 2);
        if (!int.TryParse(parts[0].Trim(), out tile) || tile < 0)
        {
            return false;
        }

        if (parts.Length > 1
            && (!int.TryParse(parts[1].Trim(), out tileLayerId) || tileLayerId < 0))
        {
            return false;
        }

        return true;
    }

    private static bool IsSameAnchor(ColonyAnchor expected, ColonyAnchor actual)
    {
        if (!string.IsNullOrWhiteSpace(expected.WorldObjectId)
            && !string.IsNullOrWhiteSpace(actual.WorldObjectId)
            && string.Equals(expected.WorldObjectId, actual.WorldObjectId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(expected.MapUniqueId)
            && !string.IsNullOrWhiteSpace(actual.MapUniqueId)
            && string.Equals(expected.MapUniqueId, actual.MapUniqueId, StringComparison.Ordinal))
        {
            return true;
        }

        return expected.Tile == actual.Tile
            && expected.TileLayerId == actual.TileLayerId
            && string.IsNullOrWhiteSpace(expected.WorldObjectId)
            && string.IsNullOrWhiteSpace(expected.MapUniqueId);
    }

    private static SnapshotUploadResult? ValidateEnvelope(SnapshotUploadContext context, SaveSnapshotEnvelope envelope)
    {
        if (envelope.PackageVersion != SaveSnapshotPackageBuilder.CurrentPackageVersion)
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.InvalidPayload,
                "Snapshot package version is not supported.");
        }

        SnapshotIdentity identity = envelope.Identity;
        if (string.IsNullOrWhiteSpace(identity.OwnerId)
            || string.IsNullOrWhiteSpace(identity.ColonyId)
            || string.IsNullOrWhiteSpace(identity.SnapshotId))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.MissingIdentity,
                "Snapshot is missing player, colony, or snapshot id.");
        }

        if (!string.Equals(identity.OwnerId, context.OwnerId, StringComparison.Ordinal)
            || !string.Equals(identity.ColonyId, context.ColonyId, StringComparison.Ordinal)
            || !string.Equals(identity.SnapshotId, context.SnapshotId, StringComparison.Ordinal))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.IdentityMismatch,
                "Snapshot identity does not match the upload context.");
        }

        if (string.IsNullOrWhiteSpace(envelope.PayloadSha256)
            || string.IsNullOrWhiteSpace(envelope.OriginalSha256))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.MissingHash,
                "Snapshot is missing payload or original save hash.");
        }

        if (string.IsNullOrWhiteSpace(envelope.RimWorldVersion))
        {
            return SnapshotUploadResult.Reject(
                SnapshotUploadResultKind.MissingRimWorldVersion,
                "Snapshot is missing the RimWorld version.");
        }

        return null;
    }

    private SaveSnapshotIndex RebuildIndex(byte[] originalPayload, SnapshotIdentity identity)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"clash-of-rim-snapshot-{Guid.NewGuid():N}.rws");
        try
        {
            File.WriteAllBytes(tempPath, originalPayload);
            SaveSnapshotIndex index = RimWorldSaveIndexReader.Read(tempPath, new SaveIndexReadOptions
            {
                Identity = identity,
                Extensions = saveIndexExtensions ?? SaveIndexExtensionRegistry.Registered
            });

            return index;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static byte[] DecodePayload(byte[] payload, SnapshotPayloadEncoding encoding)
    {
        return encoding switch
        {
            SnapshotPayloadEncoding.RawRws => payload,
            SnapshotPayloadEncoding.GzipRws => Gunzip(payload),
            _ => throw new NotSupportedException($"Unsupported snapshot payload encoding: {encoding}.")
        };
    }

    private static byte[] Gunzip(byte[] payload)
    {
        using var source = new MemoryStream(payload);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        gzip.CopyTo(target);
        return target.ToArray();
    }

    private static bool HashMatches(byte[] payload, string expectedSha256)
    {
        string actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string GenerateLineageToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private sealed record ColonyAnchor(string? MapUniqueId, string? WorldObjectId, int Tile, int TileLayerId);

    private sealed record PayloadSnapshotMetadata(string? LineageSnapshotId, string? LineageToken, long? GameTicks);
}
