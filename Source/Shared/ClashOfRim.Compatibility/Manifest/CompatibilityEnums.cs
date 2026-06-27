namespace AIRsLight.ClashOfRim.Compatibility;

public enum ModCompatibilityRole
{
    Required,
    Optional,
    OptionalPureTranslation
}

public enum ModConfigComparisonMode
{
    Enforce,
    Warn,
    Ignore
}

public enum ModFileKind
{
    Assembly,
    Def,
    Patch,
    Language,
    About,
    Texture,
    Other
}

public enum CompatibilityIssueSeverity
{
    Info,
    Warning,
    Error
}

public enum CompatibilityIssueCode
{
    SchemaVersionMismatch,
    ProtocolVersionMismatch,
    RimWorldVersionMismatch,
    GameLanguageMismatch,
    DlcListMismatch,
    ModListMismatch,
    ModOrderMismatch,
    MissingMod,
    UnexpectedMod,
    AllowedPureTranslationMod,
    FileMissing,
    FileUnexpected,
    FileHashMismatch,
    FileSizeMismatch,
    ConfigVersionMismatch,
    ConfigHashMismatch,
    ConfigFileMismatch,
    ConfigFileMissing,
    ConfigFileUnexpected,
    ConfigFileWarning,
    DefSummaryMissing,
    DefSummaryUnexpected,
    DefSummaryCountMismatch,
    DefSummaryHashMismatch
}
