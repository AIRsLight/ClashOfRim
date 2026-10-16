using System.Text.Json;
using ClashOfRim.GameTests;
using RimTest.Protocol;

if (args.Length is < 3 or > 4) return 2;
var profile = args.Length == 4
    ? JsonSerializer.Deserialize<SuiteProfile>(await File.ReadAllTextAsync(args[3]), Wire.Json) ?? throw new InvalidDataException("Empty profile")
    : new SuiteProfile();
profile.Validate();
return await new GameSuite(Path.GetFullPath(args[0]), args[1], args[2], profile).RunAsync();