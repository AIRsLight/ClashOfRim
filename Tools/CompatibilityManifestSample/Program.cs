using System.Text.Json;
using AIRsLight.ClashOfRim.Compatibility;

CompatibilityManifest server = CreateBaseManifest();
CompatibilityManifest clientWithTranslation = server with
{
    ManifestId = "client-with-translation",
    Mods =
    [
        .. server.Mods,
        new ModManifestEntry
        {
            LoadOrder = 99,
            PackageId = "sample.translation.zh",
            Name = "中文翻译",
            Role = ModCompatibilityRole.OptionalPureTranslation,
            Files =
            [
                File("Languages/ChineseSimplified/Keyed/ClashOfRim.xml", 100, "translation")
            ]
        }
    ]
};

CompatibilityManifest clientWithPatch = server with
{
    ManifestId = "client-with-unapproved-patch",
    Mods =
    [
        .. server.Mods,
        new ModManifestEntry
        {
            LoadOrder = 99,
            PackageId = "sample.patch",
            Name = "未批准补丁",
            Files =
            [
                File("Patches/AddThing.xml", 100, "patch")
            ]
        }
    ]
};

var output = new
{
    AcceptedWithTranslation = CompatibilityManifestComparer.Compare(server, clientWithTranslation),
    RejectedWithPatch = CompatibilityManifestComparer.Compare(server, clientWithPatch),
    ServerManifest = server
};

Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));

static CompatibilityManifest CreateBaseManifest()
{
    return new CompatibilityManifest
    {
        ManifestId = "server-approved-001",
        ProtocolVersion = "0.1.0",
        RimWorldVersion = "1.6.4633 rev1261",
        DlcIds =
        [
            "ludeon.rimworld",
            "ludeon.rimworld.royalty",
            "ludeon.rimworld.ideology",
            "ludeon.rimworld.biotech",
            "ludeon.rimworld.anomaly",
            "ludeon.rimworld.odyssey"
        ],
        ConfigVersion = "cfg-001",
        ConfigSha256 = "config-hash",
        Mods =
        [
            new ModManifestEntry
            {
                LoadOrder = 0,
                PackageId = "ludeon.rimworld",
                Name = "Core",
                Source = "Official",
                Files =
                [
                    File("Assemblies/Assembly-CSharp.dll", 10, "core-assembly"),
                    File("Defs/ThingDefs_Items.xml", 20, "core-def")
                ]
            },
            new ModManifestEntry
            {
                LoadOrder = 1,
                PackageId = "AIRsLight.ClashOfRim",
                Name = "ClashOfRim",
                Source = "Local",
                Files =
                [
                    File("Assemblies/AIRsLight.ClashOfRim.dll", 30, "clash-assembly")
                ],
                Configs =
                [
                    new ModConfigDigest { FileName = "ClashOfRimMod.xml", Sha256 = "clash-config" }
                ]
            }
        ],
        DefSummaries =
        [
            new DefSummary { Name = "ThingDef", Count = 100, Hash = 123 },
            new DefSummary { Name = "PawnKindDef", Count = 20, Hash = 456 }
        ]
    };
}

static ControlledFileEntry File(string path, long size, string hash)
{
    return new ControlledFileEntry
    {
        RelativePath = path,
        Size = size,
        Sha256 = hash,
        Kind = ModFileClassifier.Classify(path)
    };
}
