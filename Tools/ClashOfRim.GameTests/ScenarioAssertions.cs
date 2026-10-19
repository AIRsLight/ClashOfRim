using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal static class ScenarioAssertions
{
    internal static JsonElement[] Items(JsonElement p) => p.GetProperty("items").EnumerateArray().ToArray();
    internal static string H(JsonElement v) => v.GetProperty("reference").String("handle");
    internal static void Check(bool ok, string reason) => RimTest.Testing.Check.That(ok, reason);
    internal static void Equal(JsonElement left, JsonElement right, string message)
    {
        if (JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()))) return;
        string Summary(JsonElement value) => value.GetRawText().Length <= 1500 ? value.GetRawText()
            : "sha256=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
        Check(false, message + "\nExpected: " + Summary(left) + "\nActual: " + Summary(right));
    }
}
