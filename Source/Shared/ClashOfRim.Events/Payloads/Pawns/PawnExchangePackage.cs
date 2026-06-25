using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Events;

public sealed record PawnExchangePackage(
    int PackageVersion,
    CrossMapPawnReference Reference,
    PawnExchangeIdentity Identity,
    PawnExchangeAppearance Appearance,
    PawnExchangeStatus Status,
    IReadOnlyList<PawnExchangeEquipmentItem> Apparel,
    IReadOnlyList<PawnExchangeEquipmentItem> Equipment,
    IReadOnlyList<PawnExchangeRelationshipStub> Relationships,
    PawnScribePayload? Scribe = null,
    IReadOnlyList<PawnExchangeExtensionPackage>? Extensions = null);

public sealed record PawnExchangeExtensionPackage(
    string ProviderId,
    string Kind,
    Dictionary<string, string?> Metadata,
    string? PayloadJson = null);

public sealed record PawnExchangeIdentity(
    string? ThingDef,
    string? PawnKindDef,
    string? FactionDef,
    string? Gender);

public sealed record PawnExchangeAppearance(
    string? DisplayName,
    string? BodyTypeDef,
    string? HeadTypeDef,
    string? HairDef,
    string? BeardDef,
    string? SkinColor,
    string? HairColor);

public sealed record PawnExchangeStatus(
    bool Dead,
    long? BiologicalAgeTicks,
    long? ChronologicalAgeTicks,
    string? DeathCauseDef,
    string? HealthState);

public sealed record PawnExchangeEquipmentItem(
    string GlobalId,
    string? Def,
    string? Label,
    int StackCount,
    string? Quality,
    int? HitPoints,
    bool? WornByCorpse,
    bool? Biocoded,
    string? BiocodedPawnGlobalId,
    bool? UniqueWeapon,
    string? UniqueWeaponName,
    IReadOnlyList<string>? UniqueWeaponTraits);

public sealed record PawnExchangeRelationshipStub(
    string OtherPawnGlobalId,
    string? OtherPawnName,
    bool OtherPawnDead,
    string? RelationDef);

public sealed record PawnScribePayload(
    string Xml,
    string? XmlSha256,
    IReadOnlyList<PawnScribePawnReferenceReplacement> PawnReferenceReplacements);

public sealed record PawnScribePawnReferenceReplacement(
    string SourceLoadId,
    string PlaceholderLoadId,
    CrossMapPawnReference Reference);

public sealed record PawnScribeImportXmlResult(
    bool Accepted,
    string? Xml,
    int ReplacementCount,
    string? Error)
{
    public static PawnScribeImportXmlResult Accept(string xml, int replacementCount)
    {
        return new PawnScribeImportXmlResult(true, xml, replacementCount, null);
    }

    public static PawnScribeImportXmlResult Reject(string error)
    {
        return new PawnScribeImportXmlResult(false, null, 0, error);
    }
}

public sealed record PawnExchangeReadResult(
    bool Accepted,
    PawnExchangePackage? Package,
    string? Error)
{
    public static PawnExchangeReadResult Accept(PawnExchangePackage package)
    {
        return new PawnExchangeReadResult(true, package, null);
    }

    public static PawnExchangeReadResult Reject(string error)
    {
        return new PawnExchangeReadResult(false, null, error);
    }
}

public static class SafePawnExchangeSerializer
{
    public const int CurrentPackageVersion = 1;
    public const int MaxJsonBytes = 2 * 1024 * 1024;
    public const int MaxScribeXmlBytes = 1024 * 1024;

    private const int MaxStringLength = 512;
    private const int MaxListItems = 128;
    private const int MaxTraitsPerItem = 16;
    private const int MaxJsonDepth = 16;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false
    };

    private static readonly HashSet<string> PackageProperties = new(StringComparer.Ordinal)
    {
        "PackageVersion",
        "Reference",
        "Identity",
        "Appearance",
        "Status",
        "Apparel",
        "Equipment",
        "Relationships",
        "Scribe",
        "Extensions"
    };

    private static readonly HashSet<string> ReferenceProperties = new(StringComparer.Ordinal)
    {
        "GlobalId",
        "SourceSnapshotId",
        "Name",
        "Dead",
        "Faction",
        "Metadata"
    };

    private static readonly HashSet<string> IdentityProperties = new(StringComparer.Ordinal)
    {
        "ThingDef",
        "PawnKindDef",
        "FactionDef",
        "Gender"
    };

    private static readonly HashSet<string> AppearanceProperties = new(StringComparer.Ordinal)
    {
        "DisplayName",
        "BodyTypeDef",
        "HeadTypeDef",
        "HairDef",
        "BeardDef",
        "SkinColor",
        "HairColor"
    };

    private static readonly HashSet<string> StatusProperties = new(StringComparer.Ordinal)
    {
        "Dead",
        "BiologicalAgeTicks",
        "ChronologicalAgeTicks",
        "DeathCauseDef",
        "HealthState"
    };

    private static readonly HashSet<string> EquipmentProperties = new(StringComparer.Ordinal)
    {
        "GlobalId",
        "Def",
        "Label",
        "StackCount",
        "Quality",
        "HitPoints",
        "WornByCorpse",
        "Biocoded",
        "BiocodedPawnGlobalId",
        "UniqueWeapon",
        "UniqueWeaponName",
        "UniqueWeaponTraits"
    };

    private static readonly HashSet<string> RelationshipProperties = new(StringComparer.Ordinal)
    {
        "OtherPawnGlobalId",
        "OtherPawnName",
        "OtherPawnDead",
        "RelationDef"
    };

    private static readonly HashSet<string> ScribeProperties = new(StringComparer.Ordinal)
    {
        "Xml",
        "XmlSha256",
        "PawnReferenceReplacements"
    };

    private static readonly HashSet<string> ExtensionPackageProperties = new(StringComparer.Ordinal)
    {
        "ProviderId",
        "Kind",
        "Metadata",
        "PayloadJson"
    };

    private static readonly HashSet<string> ScribeReferenceReplacementProperties = new(StringComparer.Ordinal)
    {
        "SourceLoadId",
        "PlaceholderLoadId",
        "Reference"
    };

    private static readonly HashSet<string> DangerousPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$type",
        "$values",
        "$id",
        "$ref",
        "typeName",
        "assembly",
        "assemblyName",
        "assemblyQualifiedName",
        "serializedXml",
            "rawXml",
            "binary",
            "payload"
    };

    public static string Serialize(PawnExchangePackage package)
    {
        ValidatePackage(package);

        string json = JsonSerializer.Serialize(package, Options);
        if (JsonByteCount(json) > MaxJsonBytes)
        {
            throw new InvalidOperationException($"Pawn exchange package is too large: {JsonByteCount(json)} bytes.");
        }

        return json;
    }

    public static PawnExchangeReadResult Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return PawnExchangeReadResult.Reject("Pawn exchange package is empty.");
        }

        if (JsonByteCount(json) > MaxJsonBytes)
        {
            return PawnExchangeReadResult.Reject($"Pawn exchange package exceeds the {MaxJsonBytes} byte limit.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = MaxJsonDepth
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return PawnExchangeReadResult.Reject("Pawn exchange package root must be an object.");
            }

            string? schemaError = ValidateSchema(document.RootElement, PackageProperties, "package");
            if (schemaError is not null)
            {
                return PawnExchangeReadResult.Reject(schemaError);
            }

            PawnExchangePackage? package = document.RootElement.Deserialize<PawnExchangePackage>(Options);
            if (package is null)
            {
                return PawnExchangeReadResult.Reject("Pawn exchange package parse failed.");
            }

            ValidatePackage(package);
            return PawnExchangeReadResult.Accept(package);
        }
        catch (JsonException ex)
        {
            return PawnExchangeReadResult.Reject($"Pawn exchange package JSON is invalid: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return PawnExchangeReadResult.Reject(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return PawnExchangeReadResult.Reject(ex.Message);
        }
    }

    private static string? ValidateSchema(JsonElement element, HashSet<string> allowed, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return $"{path} must be an object.";
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string propertyName = property.Name;
            if (DangerousPropertyNames.Contains(propertyName))
            {
                return $"{path}.{propertyName} is forbidden.";
            }

            if (!allowed.Contains(propertyName))
            {
                return $"{path}.{propertyName} is not an allowed field.";
            }

            string? childError = ValidateChildSchema(property.Value, $"{path}.{propertyName}", propertyName);
            if (childError is not null)
            {
                return childError;
            }
        }

        return null;
    }

    private static string? ValidateChildSchema(JsonElement value, string path, string propertyName)
    {
        return propertyName switch
        {
            "Reference" => ValidateSchema(value, ReferenceProperties, path),
            "Identity" => ValidateSchema(value, IdentityProperties, path),
            "Appearance" => ValidateSchema(value, AppearanceProperties, path),
            "Status" => ValidateSchema(value, StatusProperties, path),
            "Scribe" => ValidateNullableObjectSchema(value, ScribeProperties, path),
            "Extensions" => ValidateNullableArraySchema(value, ExtensionPackageProperties, path),
            "PawnReferenceReplacements" => ValidateArraySchema(value, ScribeReferenceReplacementProperties, path),
            "Metadata" => ValidateMetadataSchema(value, path),
            "PayloadJson" => ValidateExtensionPayloadJsonValue(value, path),
            "Xml" => ValidateScribeXmlValue(value, path),
            "Apparel" or "Equipment" => ValidateArraySchema(value, EquipmentProperties, path),
            "Relationships" => ValidateArraySchema(value, RelationshipProperties, path),
            "UniqueWeaponTraits" => ValidateStringArray(value, path),
            _ => ValidatePrimitiveOrStringArray(value, path)
        };
    }

    private static string? ValidateNullableObjectSchema(JsonElement value, HashSet<string> allowed, string path)
    {
        return value.ValueKind == JsonValueKind.Null
            ? null
            : ValidateSchema(value, allowed, path);
    }

    private static string? ValidateNullableArraySchema(JsonElement value, HashSet<string> allowed, string path)
    {
        return value.ValueKind == JsonValueKind.Null
            ? null
            : ValidateArraySchema(value, allowed, path);
    }

    private static string? ValidateArraySchema(JsonElement value, HashSet<string> allowed, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return $"{path} must be an array.";
        }

        int count = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            count++;
            if (count > MaxListItems)
            {
                return $"{path} exceeds the {MaxListItems} item limit.";
            }

            string? error = ValidateSchema(item, allowed, $"{path}[{count - 1}]");
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static string? ValidateMetadataSchema(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return $"{path} must be an object.";
        }

        int count = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            count++;
            if (count > MaxListItems)
            {
                return $"{path} exceeds the {MaxListItems} item limit.";
            }

            if (DangerousPropertyNames.Contains(property.Name))
            {
                return $"{path}.{property.Name} is forbidden.";
            }

            string? keyError = ValidateStringValue(property.Name, $"{path}.key");
            if (keyError is not null)
            {
                return keyError;
            }

            if (property.Value.ValueKind != JsonValueKind.String && property.Value.ValueKind != JsonValueKind.Null)
            {
                return $"{path}.{property.Name} must be a string.";
            }

            string? valueError = property.Value.ValueKind == JsonValueKind.Null
                ? null
                : ValidateStringValue(property.Value.GetString(), $"{path}.{property.Name}");
            if (valueError is not null)
            {
                return valueError;
            }
        }

        return null;
    }

    private static string? ValidateStringArray(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return $"{path} must be a string array.";
        }

        int count = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            count++;
            if (count > MaxTraitsPerItem)
            {
                return $"{path} exceeds the {MaxTraitsPerItem} item limit.";
            }

            if (item.ValueKind != JsonValueKind.String)
            {
                return $"{path}[{count - 1}] must be a string.";
            }

            string? error = ValidateStringValue(item.GetString(), path);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static string? ValidatePrimitiveOrStringArray(JsonElement value, string path)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => $"{path} does not allow nested objects.",
            JsonValueKind.Array => ValidateStringArray(value, path),
            JsonValueKind.String => ValidateStringValue(value.GetString(), path),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => null,
            _ => $"{path} has an unsupported JSON type."
        };
    }

    private static string? ValidateStringValue(string? value, string path)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > MaxStringLength)
        {
            return $"{path} exceeds the {MaxStringLength} character limit.";
        }

        if (value.Any(char.IsControl))
        {
            return $"{path} contains control characters.";
        }

        return null;
    }

    private static string? ValidateScribeXmlValue(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return $"{path} must be a string.";
        }

        string? xml = value.GetString();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return $"{path} cannot be empty.";
        }

        if (XmlByteCount(xml) > MaxScribeXmlBytes)
        {
            return $"{path} exceeds the {MaxScribeXmlBytes} byte limit.";
        }

        return null;
    }

    private static string? ValidateExtensionPayloadJsonValue(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return $"{path} must be a string.";
        }

        string? payload = value.GetString();
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        if (JsonByteCount(payload) > MaxScribeXmlBytes)
        {
            return $"{path} exceeds the {MaxScribeXmlBytes} byte limit.";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        }
        catch (JsonException ex)
        {
            return $"{path} must contain valid JSON: {ex.Message}";
        }

        return null;
    }

    private static void ValidatePackage(PawnExchangePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (package.PackageVersion != CurrentPackageVersion)
        {
            throw new InvalidOperationException($"Unsupported pawn exchange package version: {package.PackageVersion}.");
        }

        ValidateReference(package.Reference);
        ValidateIdentity(package.Identity);
        ValidateAppearance(package.Appearance);
        ValidateStatus(package.Status);
        ValidateEquipmentList(package.Apparel, nameof(package.Apparel));
        ValidateEquipmentList(package.Equipment, nameof(package.Equipment));
        ValidateRelationships(package.Relationships);
        ValidateExtensions(package.Extensions);
        ValidateScribe(package.Scribe);
    }

    private static void ValidateReference(CrossMapPawnReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RequireText(reference.GlobalId, "reference.globalId");
        OptionalText(reference.SourceSnapshotId, "reference.sourceSnapshotId");
        OptionalText(reference.Name, "reference.name");
        OptionalText(reference.Faction, "reference.faction");
        ValidateMetadata(reference.Metadata, "reference.metadata");
    }

    private static void ValidateIdentity(PawnExchangeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        OptionalText(identity.ThingDef, "identity.thingDef");
        OptionalText(identity.PawnKindDef, "identity.pawnKindDef");
        OptionalText(identity.FactionDef, "identity.factionDef");
        OptionalText(identity.Gender, "identity.gender");
    }

    private static void ValidateAppearance(PawnExchangeAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        OptionalText(appearance.DisplayName, "appearance.displayName");
        OptionalText(appearance.BodyTypeDef, "appearance.bodyTypeDef");
        OptionalText(appearance.HeadTypeDef, "appearance.headTypeDef");
        OptionalText(appearance.HairDef, "appearance.hairDef");
        OptionalText(appearance.BeardDef, "appearance.beardDef");
        OptionalText(appearance.SkinColor, "appearance.skinColor");
        OptionalText(appearance.HairColor, "appearance.hairColor");
    }

    private static void ValidateStatus(PawnExchangeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        OptionalNonNegative(status.BiologicalAgeTicks, "status.biologicalAgeTicks");
        OptionalNonNegative(status.ChronologicalAgeTicks, "status.chronologicalAgeTicks");
        OptionalText(status.DeathCauseDef, "status.deathCauseDef");
        OptionalText(status.HealthState, "status.healthState");
    }

    private static void ValidateEquipmentList(IReadOnlyList<PawnExchangeEquipmentItem>? items, string label)
    {
        if (items is null)
        {
            throw new InvalidOperationException($"{label} cannot be empty.");
        }

        if (items.Count > MaxListItems)
        {
            throw new InvalidOperationException($"{label} exceeds the {MaxListItems} item limit.");
        }

        foreach (PawnExchangeEquipmentItem item in items)
        {
            RequireText(item.GlobalId, $"{label}.globalId");
            OptionalText(item.Def, $"{label}.def");
            OptionalText(item.Label, $"{label}.label");
            if (item.StackCount < 0)
            {
                throw new InvalidOperationException($"{label}.stackCount cannot be negative.");
            }

            OptionalText(item.Quality, $"{label}.quality");
            OptionalNonNegative(item.HitPoints, $"{label}.hitPoints");
            OptionalText(item.BiocodedPawnGlobalId, $"{label}.biocodedPawnGlobalId");
            OptionalText(item.UniqueWeaponName, $"{label}.uniqueWeaponName");
            ValidateStringList(item.UniqueWeaponTraits, $"{label}.uniqueWeaponTraits", MaxTraitsPerItem);
        }
    }

    private static void ValidateRelationships(IReadOnlyList<PawnExchangeRelationshipStub>? relationships)
    {
        if (relationships is null)
        {
            throw new InvalidOperationException("relationships cannot be empty.");
        }

        if (relationships.Count > MaxListItems)
        {
            throw new InvalidOperationException($"relationships exceeds the {MaxListItems} item limit.");
        }

        foreach (PawnExchangeRelationshipStub relationship in relationships)
        {
            RequireText(relationship.OtherPawnGlobalId, "relationships.otherPawnGlobalId");
            OptionalText(relationship.OtherPawnName, "relationships.otherPawnName");
            OptionalText(relationship.RelationDef, "relationships.relationDef");
        }
    }

    private static void ValidateMetadata(IReadOnlyDictionary<string, string?>? metadata, string label)
    {
        if (metadata is null)
        {
            return;
        }

        if (metadata.Count > MaxListItems)
        {
            throw new InvalidOperationException($"{label} exceeds the {MaxListItems} item limit.");
        }

        foreach (KeyValuePair<string, string?> entry in metadata)
        {
            RequireText(entry.Key, $"{label}.key");
            OptionalText(entry.Value, $"{label}.{entry.Key}");
        }
    }

    private static void ValidateScribe(PawnScribePayload? scribe)
    {
        if (scribe is null)
        {
            return;
        }

        RequireScribeXml(scribe.Xml, "scribe.xml");
        OptionalSha256(scribe.XmlSha256, "scribe.xmlSha256");
        if (!string.IsNullOrWhiteSpace(scribe.XmlSha256))
        {
            string actualHash = ComputeScribeXmlSha256(scribe.Xml);
            if (!string.Equals(actualHash, scribe.XmlSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("scribe.xmlSha256 does not match scribe.xml.");
            }
        }

        if (scribe.PawnReferenceReplacements is null)
        {
            throw new InvalidOperationException("scribe.pawnReferenceReplacements cannot be empty.");
        }

        if (scribe.PawnReferenceReplacements.Count > MaxListItems)
        {
            throw new InvalidOperationException($"scribe.pawnReferenceReplacements exceeds the {MaxListItems} item limit.");
        }

        foreach (PawnScribePawnReferenceReplacement replacement in scribe.PawnReferenceReplacements)
        {
            RequireText(replacement.SourceLoadId, "scribe.pawnReferenceReplacements.sourceLoadId");
            RequireText(replacement.PlaceholderLoadId, "scribe.pawnReferenceReplacements.placeholderLoadId");
            ValidateReference(replacement.Reference);
        }
    }

    private static void ValidateExtensions(IReadOnlyList<PawnExchangeExtensionPackage>? extensions)
    {
        if (extensions is null)
        {
            return;
        }

        if (extensions.Count > MaxListItems)
        {
            throw new InvalidOperationException($"extensions exceeds the {MaxListItems} item limit.");
        }

        foreach (PawnExchangeExtensionPackage extension in extensions)
        {
            RequireText(extension.ProviderId, "extensions.providerId");
            RequireText(extension.Kind, "extensions.kind");
            ValidateMetadata(extension.Metadata, "extensions.metadata");
            OptionalExtensionPayloadJson(extension.PayloadJson, "extensions.payloadJson");
        }
    }

    public static string ComputeScribeXmlSha256(string xml)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();
    }

    public static PawnScribeImportXmlResult RewriteScribePawnReferencesForImport(PawnScribePayload scribe)
    {
        try
        {
            ValidateScribe(scribe);

            Dictionary<string, string> replacements = scribe.PawnReferenceReplacements
                .GroupBy(replacement => replacement.SourceLoadId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().PlaceholderLoadId, StringComparer.Ordinal);
            if (replacements.Count == 0)
            {
                return PawnScribeImportXmlResult.Accept(scribe.Xml, 0);
            }

            XDocument document = ParseScribeXml(scribe.Xml);
            if (document.Root is null)
            {
                return PawnScribeImportXmlResult.Reject("scribe.xml root node is empty.");
            }

            int replacementCount = 0;
            XElement root = document.Root;
            foreach (XText text in document.DescendantNodes().OfType<XText>())
            {
                XElement? parent = text.Parent;
                if (parent is null || IsInsideNestedPawnObject(parent, root))
                {
                    continue;
                }

                if (replacements.TryGetValue(text.Value, out string? placeholderLoadId))
                {
                    text.Value = placeholderLoadId;
                    replacementCount++;
                }
            }

            foreach (XAttribute attribute in document.Descendants().Attributes())
            {
                if (string.Equals(attribute.Name.LocalName, "Class", StringComparison.Ordinal)
                    || IsInsideNestedPawnObject(attribute.Parent, root))
                {
                    continue;
                }

                if (replacements.TryGetValue(attribute.Value, out string? placeholderLoadId))
                {
                    attribute.Value = placeholderLoadId;
                    replacementCount++;
                }
            }

            return PawnScribeImportXmlResult.Accept(document.ToString(SaveOptions.DisableFormatting), replacementCount);
        }
        catch (XmlException ex)
        {
            return PawnScribeImportXmlResult.Reject($"scribe.xml is not valid XML: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return PawnScribeImportXmlResult.Reject(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return PawnScribeImportXmlResult.Reject(ex.Message);
        }
    }

    private static XDocument ParseScribeXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxScribeXmlBytes,
            XmlResolver = null
        };

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static bool IsInsideNestedPawnObject(XElement? element, XElement root)
    {
        if (element is null)
        {
            return false;
        }

        foreach (XElement ancestor in element.Ancestors())
        {
            if (ancestor == root)
            {
                continue;
            }

            if (LooksLikePawnObject(ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikePawnObject(XElement element)
    {
        XAttribute? classAttribute = element.Attribute("Class");
        if (classAttribute is not null
            && (string.Equals(classAttribute.Value, "Pawn", StringComparison.Ordinal)
                || classAttribute.Value.EndsWith(".Pawn", StringComparison.Ordinal)))
        {
            return true;
        }

        return element.Element("id") is not null && element.Element("kindDef") is not null;
    }

    private static void ValidateStringList(IReadOnlyList<string>? values, string label, int maxItems)
    {
        if (values is null)
        {
            return;
        }

        if (values.Count > maxItems)
        {
            throw new InvalidOperationException($"{label} exceeds the {maxItems} item limit.");
        }

        foreach (string value in values)
        {
            OptionalText(value, label);
        }
    }

    private static void RequireText(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{label} cannot be empty.");
        }

        OptionalText(value, label);
    }

    private static void RequireScribeXml(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{label} cannot be empty.");
        }

        if (XmlByteCount(value) > MaxScribeXmlBytes)
        {
            throw new InvalidOperationException($"{label} exceeds the {MaxScribeXmlBytes} byte limit.");
        }
    }

    private static void OptionalExtensionPayloadJson(string? value, string label)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (JsonByteCount(value) > MaxScribeXmlBytes)
        {
            throw new InvalidOperationException($"{label} exceeds the {MaxScribeXmlBytes} byte limit.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{label} must contain valid JSON: {ex.Message}");
        }
    }

    private static void OptionalText(string? value, string label)
    {
        string? error = ValidateStringValue(value, label);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    private static void OptionalNonNegative(long? value, string label)
    {
        if (value < 0)
        {
            throw new InvalidOperationException($"{label} cannot be negative.");
        }
    }

    private static void OptionalNonNegative(int? value, string label)
    {
        if (value < 0)
        {
            throw new InvalidOperationException($"{label} cannot be negative.");
        }
    }

    private static void OptionalSha256(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidOperationException($"{label} must be a 64-character hexadecimal SHA-256.");
        }
    }

    private static int JsonByteCount(string json)
    {
        return Encoding.UTF8.GetByteCount(json);
    }

    private static int XmlByteCount(string xml)
    {
        return Encoding.UTF8.GetByteCount(xml);
    }
}
