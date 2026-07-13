namespace AIRsLight.ClashOfRim.Compatibility;

public enum CompatibilityIssueCategory
{
    Manifest,
    Hash,
    Config,
    Overview
}

public static class CompatibilityIssueClassifier
{
    public static CompatibilityIssueCategory CategoryFor(CompatibilityIssueCode code)
    {
        return code switch
        {
            CompatibilityIssueCode.FileMissing
                or CompatibilityIssueCode.FileUnexpected
                or CompatibilityIssueCode.FileHashMismatch
                or CompatibilityIssueCode.FileSizeMismatch
                or CompatibilityIssueCode.DefSummaryMissing
                or CompatibilityIssueCode.DefSummaryUnexpected
                or CompatibilityIssueCode.DefSummaryCountMismatch
                or CompatibilityIssueCode.DefSummaryHashMismatch => CompatibilityIssueCategory.Hash,
            CompatibilityIssueCode.ConfigVersionMismatch
                or CompatibilityIssueCode.ConfigHashMismatch
                or CompatibilityIssueCode.ConfigFileMismatch
                or CompatibilityIssueCode.ConfigFileMissing
                or CompatibilityIssueCode.ConfigFileUnexpected
                or CompatibilityIssueCode.ConfigFileWarning => CompatibilityIssueCategory.Config,
            CompatibilityIssueCode.ProtocolVersionMismatch => CompatibilityIssueCategory.Overview,
            CompatibilityIssueCode.SchemaVersionMismatch
                or CompatibilityIssueCode.RimWorldVersionMismatch
                or CompatibilityIssueCode.GameLanguageMismatch
                or CompatibilityIssueCode.DlcListMismatch
                or CompatibilityIssueCode.ModListMismatch
                or CompatibilityIssueCode.ModOrderMismatch
                or CompatibilityIssueCode.MissingMod
                or CompatibilityIssueCode.UnexpectedMod
                or CompatibilityIssueCode.AllowedPureTranslationMod => CompatibilityIssueCategory.Manifest,
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown compatibility issue code.")
        };
    }
}
