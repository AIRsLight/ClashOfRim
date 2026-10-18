using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.Protocol;

namespace ClashOfRim.TestSupport;

internal static class BiotechTradeConstraintCases
{
    internal static void Run(Func<Dictionary<string, string?>?, Dictionary<string, string?>?, bool> matches,
        Func<Dictionary<string, string?>?, int> strictness, Func<Dictionary<string, string?>?, int>? descriptionCount = null)
    {
        var previous = CultureInfo.CurrentCulture;
        var cultures = new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" }.Select(name => CultureInfo.GetCultureInfo(name)).ToList();
        var custom = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        custom.NumberFormat.PositiveSign = "plus";
        cultures.Add(custom);
        var failures = new List<string>();
        try
        {
            foreach (CultureInfo culture in cultures)
            foreach (Case test in Cases())
            {
                CultureInfo.CurrentCulture = culture;
                bool actual = matches(test.Required, test.Candidate);
                int actualStrictness = strictness(test.Required);
                int? descriptions = descriptionCount?.Invoke(test.Required);
                if (actual != test.Matches || actualStrictness != test.Strictness || (descriptions.HasValue && descriptions != test.Descriptions))
                    failures.Add(culture.Name + "/sign=" + culture.NumberFormat.PositiveSign + "/" + test.Name
                        + ": match=" + actual + " (expected " + test.Matches + "), strictness=" + actualStrictness + " (expected " + test.Strictness + ")"
                        + (descriptions.HasValue ? ", descriptions=" + descriptions + " (expected " + test.Descriptions + ")" : ""));
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
        if (failures.Count != 0)
            throw new Exception("Biotech trade constraint divergence (" + failures.Count + "): " + string.Join("; ", failures.Take(8)));
    }

    private static IEnumerable<Case> Cases()
    {
        yield return new("empty", null, null, true, 0);
        yield return new("gene subset", Genes(true, " A ,a,B"), Genes(false, "b,a,C"), true, 1_000_000);
        yield return new("missing gene", Genes(true, "A,B"), Genes(false, "A"), false, 1_000_000);
        yield return new("role-only mismatch", Sources("1", (0, "role", "Mother")), Sources("1", (0, "role", "Father")), false, 900_000);
        yield return new("role-only match", Sources("1", (0, "role", "Mother")), Sources("1", (0, "role", "mother")), true, 900_000);
        yield return new("role requires source", Sources("1", (0, "role", "Mother")), null, false, 900_000);
        yield return new("identity is not a constraint", Sources("1", (0, "globalId", "pawn-A")), null, true, 0);
        yield return new("restoration state is not a constraint", Sources("1", (0, "ownerUserId", "owner"), (0, "name", "名字"), (0, "dead", "true"), (0, "factionDef", "PlayerColony")), null, true, 0);
        yield return new("identity does not satisfy a source constraint", Sources("1", (0, "role", "Mother")), Sources("1", (0, "globalId", "pawn-A")), false, 900_000);
        yield return new("source genes are equal sets", Sources("1", (0, "endogeneDefNames", "A,b,A")), Sources("1", (0, "endogeneDefNames", " B,a ")), true, 900_000);
        yield return new("source genes reject superset", Sources("1", (0, "endogeneDefNames", "A")), Sources("1", (0, "endogeneDefNames", "A,B")), false, 900_000);
        yield return new("empty source genes are wildcard", Sources("1", (0, "raceDef", "Human")), Sources("1", (0, "raceDef", "human"), (0, "endogeneDefNames", "any")), true, 900_000);
        yield return new("xenogenes are separate", Sources("1", (0, "xenogeneDefNames", "A")), Sources("1", (0, "endogeneDefNames", "A")), false, 900_000);
        yield return new("ordinal role case", Sources("1", (0, "role", "I")), Sources("1", (0, "role", "i")), true, 900_000);
        yield return new("ordinal role distinguishes dotted I", Sources("1", (0, "role", "I")), Sources("1", (0, "role", "İ")), false, 900_000);
        yield return new("two roles", Sources("2", (0, "role", "Mother"), (1, "role", "Father")), Sources("1", (0, "role", "Mother")), false, 900_000);
        yield return new("source order is irrelevant", Sources("2", (0, "role", "Mother"), (1, "role", "Father")), Sources("2", (0, "role", "Father"), (1, "role", "Mother")), true, 900_000);
        yield return new("repeated requirements are existential", Sources("2", (0, "role", "Mother"), (1, "role", "Mother")), Sources("1", (0, "role", "Mother")), true, 900_000);
        yield return new("four source limit", Sources("5", (4, "role", "Mother")), null, true, 0);
        yield return new("last supported source", Sources("2147483647", (3, "role", "Mother")), null, false, 900_000);
        yield return new("blank source", Sources("1", (0, "role", " "), (0, "endogeneDefNames", ", ,")), null, true, 0);
        foreach (string count in new[] { "-1", "0", "invalid", "2147483648", "1,000", "plus1" })
            yield return new("invalid count " + count, Sources(count, (0, "role", "Mother")), null, true, 0);
        yield return new("invariant count sign", Sources("+1", (0, "raceDef", "Human")), Sources("1", (0, "raceDef", "Alien")), false, 900_000);
        var combined = Sources("1", (0, "role", "Mother")); combined[BiotechThingMetadataKeys.TargetGeneDefNames] = "A";
        var candidate = Sources("1", (0, "role", "Mother")); candidate[BiotechThingMetadataKeys.GeneDefNames] = "a,B";
        yield return new("combined constraints", combined, candidate, true, 1_900_000);
        yield return new("combined missing genes", combined, Sources("1", (0, "role", "Mother")), false, 1_900_000);
    }

    private sealed record Case(string Name, Dictionary<string, string?>? Required, Dictionary<string, string?>? Candidate, bool Matches, int Strictness)
    {
        internal int Descriptions => Strictness == 0 ? 0 : Strictness == 1_900_000 ? 2 : 1;
    }
    private static Dictionary<string, string?> Genes(bool required, string genes) => new()
    {
        [required ? BiotechThingMetadataKeys.TargetGeneDefNames : BiotechThingMetadataKeys.GeneDefNames] = genes
    };
    private static Dictionary<string, string?> Sources(string count, params (int Index, string Field, string? Value)[] fields)
    {
        var result = new Dictionary<string, string?> { [BiotechThingMetadataKeys.ReproductiveSourceCount] = count };
        foreach (var field in fields) result[BiotechThingMetadataKeys.ReproductiveSourcePrefix + field.Index.ToString(CultureInfo.InvariantCulture) + "." + field.Field] = field.Value;
        return result;
    }
}
