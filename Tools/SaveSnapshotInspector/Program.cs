using System.Security.Cryptography;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;
using AIRsLight.ClashOfRim.Save;

static int PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  SaveSnapshotInspector <save.rws|package.snapshot.gz> [--owner <id>] [--colony <id>] [--snapshot <id>] [--extract-rws <path>] [--settlement-sample]");
    Console.Error.WriteLine("  SaveSnapshotInspector --server-data <DataDir> --owner <id> --colony <id> [--extract-rws <path>] [--settlement-sample]");
    return 2;
}

if (args.Length == 0)
{
    return PrintUsage();
}

string? inputPath = args[0].StartsWith("--", StringComparison.Ordinal) ? null : args[0];
string? serverDataPath = ReadOption(args, "--server-data");
string? ownerId = ReadOption(args, "--owner");
string? colonyId = ReadOption(args, "--colony");
string? snapshotId = ReadOption(args, "--snapshot");
string? extractRwsPath = ReadOption(args, "--extract-rws");
bool includeSettlementSample = ReadFlag(args, "--settlement-sample");
IReadOnlyList<ISaveIndexExtension> extensions = new ISaveIndexExtension[] { new IdeologySaveIndexExtension() };

try
{
    object output;
    if (!string.IsNullOrWhiteSpace(serverDataPath))
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(colonyId))
        {
            Console.Error.WriteLine("--server-data requires --owner and --colony.");
            return 2;
        }

        string? packagePath = SaveSnapshotPackageFileReader.FindPackagePath(serverDataPath, ownerId!, colonyId!);
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            Console.Error.WriteLine($"Snapshot package not found under {serverDataPath} for owner={ownerId}, colony={colonyId}.");
            return 1;
        }

        output = InspectPackage(packagePath, new SnapshotIdentity(ownerId, colonyId, snapshotId), extensions, extractRwsPath, includeSettlementSample);
    }
    else if (string.IsNullOrWhiteSpace(inputPath))
    {
        return PrintUsage();
    }
    else if (inputPath.EndsWith(SaveSnapshotPackageFileReader.PackageExtension, StringComparison.OrdinalIgnoreCase))
    {
        var identityOverride = new SnapshotIdentity(ownerId, colonyId, snapshotId);
        output = InspectPackage(inputPath, identityOverride, extensions, extractRwsPath, includeSettlementSample);
    }
    else
    {
        output = InspectRws(inputPath, new SnapshotIdentity(ownerId, colonyId, snapshotId ?? Path.GetFileNameWithoutExtension(inputPath)), extensions, includeSettlementSample);
    }

    Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions
    {
        WriteIndented = true
    }));

    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static object InspectRws(
    string savePath,
    SnapshotIdentity identity,
    IReadOnlyList<ISaveIndexExtension> extensions,
    bool includeSettlementSample)
{
    SaveSnapshotIndex index = RimWorldSaveIndexReader.Read(savePath, new SaveIndexReadOptions
    {
        Identity = identity,
        Extensions = extensions
    });

    return BuildOutput(
        inputKind: "Rws",
        inputPath: savePath,
        persisted: null,
        encodedPayload: null,
        originalPayload: null,
        index,
        identity,
        includeSettlementSample);
}

static object InspectPackage(
    string packagePath,
    SnapshotIdentity identityOverride,
    IReadOnlyList<ISaveIndexExtension> extensions,
    string? extractRwsPath,
    bool includeSettlementSample)
{
    SaveSnapshotPackageFileReadResult? result = SaveSnapshotPackageFileReader.ReadPackage(
        packagePath,
        new SaveSnapshotPackageFileReadOptions
        {
            RebuildIndex = true,
            IdentityOverride = HasCompleteIdentity(identityOverride) ? identityOverride : null,
            Extensions = extensions
        });
    if (result is null)
    {
        throw new InvalidDataException($"Snapshot package could not be read: {packagePath}");
    }

    byte[]? originalPayload = null;
    if (result.EncodedPayload is not null)
    {
        originalPayload = SaveSnapshotPackageFileReader.DecodePayload(
            result.EncodedPayload,
            result.Persisted.Envelope.PayloadEncoding);
        if (!string.IsNullOrWhiteSpace(extractRwsPath))
        {
            string? directory = Path.GetDirectoryName(extractRwsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(extractRwsPath, originalPayload);
        }
    }

    SaveSnapshotIndex index = result.RebuiltIndex ?? result.Persisted.Index;
    return BuildOutput(
        inputKind: "SnapshotPackage",
        inputPath: packagePath,
        result.Persisted,
        result.EncodedPayload,
        originalPayload,
        index,
        result.Persisted.Identity,
        includeSettlementSample,
        extractRwsPath);
}

static object BuildOutput(
    string inputKind,
    string inputPath,
    PersistedSaveSnapshotPackage? persisted,
    byte[]? encodedPayload,
    byte[]? originalPayload,
    SaveSnapshotIndex index,
    SnapshotIdentity identity,
    bool includeSettlementSample,
    string? extractedRwsPath = null)
{
    IReadOnlyList<IdeoSummary> ideos = IdeologySaveIndexExtension.ReadIdeos(index.Extensions);
    ObjectIdentityMap identityMap = ObjectIdentityMap.FromIndex(index, identity, ObjectTracePurpose.OriginalSnapshot);

    return new
    {
        InputKind = inputKind,
        InputPath = Path.GetFullPath(inputPath),
        ExtractedRwsPath = string.IsNullOrWhiteSpace(extractedRwsPath) ? null : Path.GetFullPath(extractedRwsPath),
        Package = persisted is null ? null : new
        {
            persisted.AcceptedAtUtc,
            persisted.Identity,
            persisted.Envelope,
            AcceptedOriginalSha256Count = persisted.AcceptedOriginalSha256?.Count ?? 0,
            AcceptedOriginalSha256 = persisted.AcceptedOriginalSha256,
            HasEncodedPayload = encodedPayload is not null,
            EncodedPayloadBytes = encodedPayload?.LongLength,
            EncodedPayloadSha256 = encodedPayload is null ? null : Sha256Hex(encodedPayload),
            OriginalPayloadBytes = originalPayload?.LongLength,
            OriginalPayloadSha256 = originalPayload is null ? null : Sha256Hex(originalPayload),
            PayloadHashMatchesEnvelope = encodedPayload is null
                ? (bool?)null
                : string.Equals(Sha256Hex(encodedPayload), persisted.Envelope.PayloadSha256, StringComparison.OrdinalIgnoreCase),
            OriginalHashMatchesEnvelope = originalPayload is null
                ? (bool?)null
                : string.Equals(Sha256Hex(originalPayload), persisted.Envelope.OriginalSha256, StringComparison.OrdinalIgnoreCase)
        },
        Index = new
        {
            index.SavePath,
            index.Meta.GameVersion,
            ModCount = index.Meta.ModIds.Count,
            FactionCount = index.Factions.Count,
            IdeoCount = ideos.Count,
            SaveIndexExtensionCount = index.Extensions.Count,
            WorldObjectCount = index.WorldObjects.Count,
            MapCount = index.Maps.Count,
            ThingCount = index.Things.Count,
            PawnCount = index.Pawns.Count,
            Maps = index.Maps,
            Factions = index.Factions,
            FirstThings = index.Things.Take(5),
            FirstPawns = index.Pawns.Take(5),
            FirstIdentityMappings = identityMap.Mappings.Take(5),
            SettlementSample = includeSettlementSample ? BuildSettlementSample(index.Things) : null
        }
    };
}

static string? ReadOption(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    if (index < 0 || index + 1 >= args.Length)
    {
        return null;
    }

    return args[index + 1];
}

static bool ReadFlag(string[] args, string name)
{
    return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
}

static bool HasCompleteIdentity(SnapshotIdentity identity)
{
    return !string.IsNullOrWhiteSpace(identity.OwnerId)
        && !string.IsNullOrWhiteSpace(identity.ColonyId)
        && !string.IsNullOrWhiteSpace(identity.SnapshotId);
}

static string Sha256Hex(byte[] payload)
{
    byte[] hash = SHA256.HashData(payload);
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static object? BuildSettlementSample(IReadOnlyList<ThingSummary> things)
{
    var originalThings = things.Take(10).ToList();
    if (originalThings.Count < 4)
    {
        return null;
    }

    originalThings[3] = originalThings[3] with { StackCount = "75" };
    var returnedThings = originalThings.Skip(3).ToList();
    returnedThings[0] = returnedThings[0] with { StackCount = "70" };
    returnedThings.Add(originalThings[0] with
    {
        LocalId = $"{originalThings[0].LocalId}_extra",
        GlobalKey = $"{originalThings[0].GlobalKey}/extra"
    });

    var diff = RaidSettlementDiffer.CompareByDisappearance(
        originalThings,
        returnedThings,
        new RaidSettlementPolicy(0.5, "sample-raid-event"));

    return new
    {
        diff.LossRatio,
        diff.StolenThingCount,
        diff.ReducedStackThingCount,
        diff.TotalStolenStackCount,
        diff.TotalLossCount,
        diff.IgnoredExtraThingCount,
        MissingGlobalKeys = diff.MissingThings.Select(thing => thing.GlobalKey),
        Losses = diff.Losses.Select(loss => new
        {
            loss.Thing.GlobalKey,
            loss.OriginalStackCount,
            loss.ReturnedStackCount,
            loss.StolenStackCount,
            loss.BaseLossCapCount,
            loss.FractionalCapChance,
            loss.FractionalRoll,
            loss.MaxLossCount,
            loss.LossCount
        })
    };
}
