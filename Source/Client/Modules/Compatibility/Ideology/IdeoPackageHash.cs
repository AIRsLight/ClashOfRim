using System;
using System.Security.Cryptography;
using System.Text;

namespace AIRsLight.ClashOfRim.Modules.Compatibility.Ideology;

internal static class IdeoPackageHash
{
    internal static string Compute(string xml) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();
}
