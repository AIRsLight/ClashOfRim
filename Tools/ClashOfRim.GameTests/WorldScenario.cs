using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class WorldScenario(TestRun run, ScenarioPolling polling, SuiteProfile profile,
    WorldConfigurationRegistry world, IWorldTileDistanceCalculator distances,
    Func<string, Task<WorldPersistenceEvidence>> readPersisted)
{
    private JsonElement expectedDifficulty, expectedWorld;
    private JsonElement worldA, worldB;
    internal void VerifyLaunch(string client, JsonElement hello)
    {
        var expected = client == "A" ? profile.A : profile.B;
        Check(hello.String("culture") == expected.Culture && hello.String("uiCulture") == expected.Culture && hello.String("language") == expected.Language,
            $"{client} did not launch with requested language/culture: {hello.OptionalString("language")}/{hello.OptionalString("culture")}");
        var actualDlc = hello.GetProperty("mods").EnumerateArray().Select(m => m.String("id"))
            .Where(id => id.StartsWith("ludeon.rimworld.", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
        Check(actualDlc.SequenceEqual(profile.Expansions.Select(e => "ludeon.rimworld." + e.ToLowerInvariant()).Order(StringComparer.Ordinal)), "Active DLC set differs from the profile.");
    }
    internal async Task VerifySharedEntry()
    {
        Check(world.Current is not null, "No persisted server world baseline.");
        var worldA = await run.CallAsync("A", "clash.world"); var worldB = await run.CallAsync("B", "clash.world");
        Check(!string.IsNullOrWhiteSpace(worldA.OptionalString("worldConfigurationId")) && worldA.GetRawText() == worldB.GetRawText(), "Clients did not apply the same authoritative world identity/topology.");
        var a = (await run.CallAsync("A", "clash.inventory")).GetProperty("maps")[0];
        var b = (await run.CallAsync("B", "clash.inventory")).GetProperty("maps")[0];
        Check(a.Int("tileId") != b.Int("tileId"), "Clients occupied the same colony tile.");
        var sites = world.Current!.PlayerColonySites;
        Check(sites.Any(s => s.UserId == "rimtest-A" && s.Tile == a.Int("tileId")) &&
            sites.Any(s => s.UserId == "rimtest-B" && s.Tile == b.Int("tileId")), "Both colony sites must be registered authoritatively.");
    }
    internal async Task ConfigureWorld(string client, string page)
    {
        var options = await run.CallAsync(client, "setup.options");
        string Resolve(string field, string value)
        {
            var values = options.GetProperty(field).EnumerateArray().Select(v => v.GetString()!).ToArray();
            return value switch
            {
                "min" => values.First(),
                "max" => values.Last(),
                _ when values.Contains(value) => value,
                _ => throw new InvalidOperationException($"Unsupported {field}: {value}; offered: {string.Join(",", values)}")
            };
        }
        var input = new Dictionary<string, object?>
        {
            ["page"] = page,
            ["seed"] = profile.Seed,
            ["coveragePercent"] = profile.Coverage,
            ["rainfall"] = Resolve("rainfall", profile.Rainfall),
            ["temperature"] = Resolve("temperature", profile.Temperature),
            ["population"] = Resolve("population", profile.Population)
        };
        if (profile.Expansions.Contains("Odyssey")) input["landmarks"] = Resolve("landmarks", profile.Landmarks);
        if (profile.Expansions.Contains("Biotech")) input["pollution"] = profile.Pollution;
        var response = await run.CallAsync(client, "setup.world", input);
        if (client == "A") expectedWorld = response;
    }
    Task Case(string id, Func<Task> body) => run.RunCaseAsync(id, body, () => run.CaptureLogsAsync());
    Task AssertCase(string id, Action body) => ObserveCase(id, () => { body(); return Task.CompletedTask; });
    async Task ObserveCase(string id, Func<Task> body)
    {
        try { await Case(id, body); }
        catch (Exception) { } // TestRun retains the failure; independent observations still run.
    }
    internal async Task Run()
    {
        worldA = await run.CallAsync("A", "clash.world.details"); worldB = await run.CallAsync("B", "clash.world.details");
        var baseline = world.Current!;
        await AssertCase("world-parameters", () =>
        {
            Check(expectedWorld.ValueKind == JsonValueKind.Object, "Host never configured a world.");
            Check(baseline.SeedString == profile.Seed && Math.Abs(float.Parse(baseline.PlanetCoverage!, CultureInfo.InvariantCulture) - profile.Coverage / 100f) < 0.000001f, "Server seed/coverage differs from requested values.");
            foreach (var world in new[] { worldA, worldB })
            {
                var info = world.GetProperty("info");
                Check(info.String("seedString") == profile.Seed, "Generated seed changed.");
                Check(Math.Abs(info.GetProperty("planetCoverage").GetDouble() - profile.Coverage / 100d) < 0.000001, "Generated coverage silently fell back.");
                foreach (string field in new[] { "rainfall", "temperature", "population" })
                    Check(info.String("overall" + char.ToUpperInvariant(field[0]) + field[1..]) == expectedWorld.String(field), "Generated " + field + " differs from host selection.");
                if (profile.Expansions.Contains("Odyssey")) Check(info.String("landmarkDensity") == expectedWorld.String("landmarks"), "Generated landmark density changed.");
                if (profile.Expansions.Contains("Biotech")) Check(Math.Abs(info.GetProperty("pollution").GetDouble() - profile.Pollution) < 0.000001, "Generated pollution silently fell back.");
            }
            Check(baseline.OverallRainfall == expectedWorld.String("rainfall") && baseline.OverallTemperature == expectedWorld.String("temperature") &&
                baseline.OverallPopulation == expectedWorld.String("population"), "Server environment parameters changed.");
        });
        await AssertCase("world-geometry", () =>
        {
            var layersA = worldA.GetProperty("layers").EnumerateArray().ToArray(); var layersB = worldB.GetProperty("layers").EnumerateArray().ToArray();
            Check(baseline.TileGeometry is not null && layersA.Length == baseline.TileGeometry.Layers.Count && layersB.Length == layersA.Length, "Missing authoritative layer geometry.");
            foreach (var layer in baseline.TileGeometry!.Layers)
            {
                var a = layersA.Single(l => l.Int("id") == layer.LayerId); var b = layersB.Single(l => l.Int("id") == layer.LayerId);
                Check(a.Int("count") > 0 && a.Int("count") == layer.TileCenters.Count && b.Int("count") == a.Int("count"), "Truncated or missing layer tiles.");
                string digest = GeometryDigest(layer);
                Check(a.String("geometrySha256") == digest && b.String("geometrySha256") == digest, "Actual tile centers differ from persisted geometry.");
            }
            Check(world.TryGetWorldSubstrate(baseline.WorldConfigurationId, out var payload) && payload is { Length: > 0 }, "Detached world substrate was never uploaded.");
            Check(WorldSubstratePackageCodec.TryDecode(payload!, out var package, out _) && package!.TileGeometryPayload.Length > 0, "Detached substrate is invalid.");
        });
        await ObserveCase("world-tile-data", async () =>
        {
            await TileDiagnostics();
            foreach (var a in worldA.GetProperty("layers").EnumerateArray())
            {
                var b = worldB.GetProperty("layers").EnumerateArray().Single(l => l.Int("id") == a.Int("id"));
                Check(a.String("tilesSha256") == b.String("tilesSha256"), "Terrain/biome/climate/neighbour data differs between clients. See tile-differences evidence.");
            }
        });
        await AssertCase("world-feature-identities", () =>
        {
            var a = worldA.GetProperty("features").EnumerateArray().ToArray(); var b = worldB.GetProperty("features").EnumerateArray().ToArray();
            Check(a.Length > 0 && a.Length == b.Length && a.Length == baseline.Features.Count, "Missing generated features.");
            foreach (var feature in a)
            {
                var other = b.Single(f => f.Int("id") == feature.Int("id"));
                Check(feature.String("defName") == other.String("defName") && !string.IsNullOrWhiteSpace(other.String("name")), "Localized feature lost canonical identity/name.");
                Equal(feature.GetProperty("center"), other.GetProperty("center"), "Localized feature moved.");
                Equal(feature.GetProperty("size"), other.GetProperty("size"), "Localized feature changed size.");
                if (profile.A.Language == profile.B.Language) Check(feature.String("name") == other.String("name"), "Same-language feature names differ.");
            }
        });
        await AssertCase("storyteller-difficulty", () =>
        {
            Check(expectedDifficulty.ValueKind == JsonValueKind.Object, "Host never selected difficulty.");
            Check(baseline.StorytellerDefName == profile.Storyteller && baseline.DifficultyDefName == profile.Difficulty, "Server storyteller/preset changed.");
            Check(!string.IsNullOrWhiteSpace(baseline.DifficultyValuesXml), "Server omitted difficulty values.");
            _ = XElement.Parse(baseline.DifficultyValuesXml!);
            foreach (var world in new[] { worldA, worldB })
            {
                Check(world.String("storyteller") == profile.Storyteller && world.String("difficulty") == profile.Difficulty, "Applied storyteller/preset differs.");
                Equal(expectedDifficulty, world.GetProperty("difficultyValues"), "Applied difficulty fields differ from original selection.");
            }
        });
        await AssertCase("culture-references", CheckCultures);
        await ObserveCase("world-persistence", async () =>
        {
            var persistedBaseline = world.Current!;
            var restored = await readPersisted(baseline.WorldConfigurationId);
            Check(restored.Configuration?.WorldConfigurationId == baseline.WorldConfigurationId, "World identity did not survive registry reconstruction.");
            var expected = JsonSerializer.SerializeToElement(persistedBaseline, Wire.Json);
            var actual = JsonSerializer.SerializeToElement(restored.Configuration, Wire.Json);
            foreach (var field in expected.EnumerateObject())
            {
                try { Equal(field.Value, actual.GetProperty(field.Name), "Persisted baseline field differs: " + field.Name); }
                catch
                {
                    await File.WriteAllTextAsync(Path.Combine(run.DirectoryPath, "world-persistence-expected.json"), expected.GetRawText());
                    await File.WriteAllTextAsync(Path.Combine(run.DirectoryPath, "world-persistence-actual.json"), actual.GetRawText());
                    throw;
                }
            }
            Check(restored.Substrate is not null && world.TryGetWorldSubstrate(baseline.WorldConfigurationId, out var original)
                && restored.Substrate.AsSpan().SequenceEqual(original), "World substrate did not survive persistence.");
        });
        await Case("storyteller-permission", async () =>
        {
            var before = await run.CallAsync("B", "clash.world.details");
            Check(!(await run.CallAsync("B", "clash.storyteller.open")).Bool("opened"), "Ordinary player opened locked storyteller settings.");
            var after = await run.CallAsync("B", "clash.world.details");
            Equal(before.GetProperty("difficultyValues"), after.GetProperty("difficultyValues"), "Denied editor changed difficulty.");
        });
        await Case("runtime-difficulty-sync", RuntimeDifficulty);
    }
    async Task RuntimeDifficulty()
    {
        async Task<JsonElement> Select(string storyteller, string difficulty, Dictionary<string, float>? values)
        {
            var page = await run.CallAsync("A", "clash.storyteller.open");
            Check(page.Bool("opened"), "Administrator could not open storyteller editor.");
            var selected = await run.CallAsync("A", "storyteller.configure", new
            {
                page = page.GetProperty("reference").String("handle"),
                storyteller,
                difficulty,
                baseDifficulty = profile.BaseDifficulty,
                values
            });
            await polling.Idle("A");
            Check(world.Current!.StorytellerDefName == storyteller && world.Current.DifficultyDefName == difficulty, "Runtime selection was not persisted.");
            var applied = await polling.Wait("B", "clash.difficulty", null, d => d.String("storyteller") == storyteller && d.String("difficulty") == difficulty, 90);
            Equal(selected.GetProperty("values"), applied.GetProperty("values"), "Runtime difficulty values differ on guest.");
            return applied;
        }
        try { await Select(profile.Storyteller == "Randy" ? "Phoebe" : "Randy", "Custom", new() { ["threatScale"] = 0.73f, ["allowInstantKillChance"] = 0.11f, ["adaptationEffectFactor"] = 0.37f }); }
        finally { await Select(profile.Storyteller, profile.Difficulty, profile.CustomValues); }
    }
    async Task TileDiagnostics()
    {
        foreach (var a in worldA.GetProperty("layers").EnumerateArray())
        {
            var b = worldB.GetProperty("layers").EnumerateArray().Single(l => l.Int("id") == a.Int("id"));
            if (a.String("tilesSha256") == b.String("tilesSha256")) continue;
            var blocks = a.GetProperty("blocks").EnumerateArray().ToArray(); var other = b.GetProperty("blocks").EnumerateArray().ToArray();
            int first = Enumerable.Range(0, blocks.Length).First(i => blocks[i].GetString() != other[i].GetString());
            var input = new { layerId = a.Int("id"), start = first * 1024, count = Math.Min(1024, a.Int("count") - first * 1024) };
            var left = (await run.CallAsync("A", "clash.world.tiles", input)).EnumerateArray().ToArray();
            var right = (await run.CallAsync("B", "clash.world.tiles", input)).EnumerateArray().ToArray();
            await run.RecordAsync(new { observation = "tile-differences", layerId = a.Int("id"), examples = left.Zip(right).Where(p => p.First.GetRawText() != p.Second.GetRawText()).Take(8).Select(p => new { host = p.First, guest = p.Second }).ToArray() });
        }
    }
    void CheckCultures()
    {
        foreach (var world in new[] { worldA, worldB })
        {
            var ideos = world.GetProperty("ideologies").EnumerateArray().ToArray();
            Check(ideos.Select(i => i.Int("id")).Distinct().Count() == ideos.Length, "Duplicate ideology IDs.");
            foreach (var pawn in world.GetProperty("pawns").EnumerateArray()) Check(pawn.Bool("ideologyRegistered"), "Pawn references an unregistered ideology.");
            if (profile.Expansions.Contains("Ideology"))
            {
                Check(ideos.Length > 0, "Ideology profile produced no ideologies.");
                foreach (var ideo in ideos) Check(!string.IsNullOrWhiteSpace(ideo.OptionalString("culture")), "Ideology lost CultureDef reference.");
            }
        }
        // Local player ideologies may differ; shared NPC factions must keep canonical culture/memes/precepts.
        foreach (string def in world.Current!.Factions.Select(f => f.DefName).Distinct(StringComparer.Ordinal))
        {
            var a = worldA.GetProperty("factions").EnumerateArray().Where(f => f.String("defName") == def).ToArray();
            var b = worldB.GetProperty("factions").EnumerateArray().Where(f => f.String("defName") == def).ToArray();
            Check(a.Length > 0 && a.Length == b.Length, "Shared faction missing/duplicated: " + def);
            string Canonical(JsonElement faction)
            {
                var ideo = faction.GetProperty("ideology");
                if (ideo.ValueKind == JsonValueKind.Null) return "null";
                return JsonSerializer.Serialize(new[] { "culture", "foundation", "memes", "precepts" }.Select(key => ideo.GetProperty(key)).ToArray());
            }
            Check(a.Select(Canonical).Order(StringComparer.Ordinal).SequenceEqual(b.Select(Canonical).Order(StringComparer.Ordinal)), "Shared faction culture/memes/precepts differ: " + def);
        }
    }
    static string GeometryDigest(WorldTileLayerGeometryDto layer)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(layer.LayerId); writer.Write(layer.TileCenters.Count);
        foreach (var tile in layer.TileCenters.OrderBy(t => t.Tile)) { writer.Write(tile.Tile); writer.Write(tile.X); writer.Write(tile.Y); writer.Write(tile.Z); }
        writer.Flush(); return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    internal async Task SelectSite(string client, string page, int? firstColonyTile)
    {
        object siteQuery = firstColonyTile is int origin
            ? new { page = page, biome = "TemperateForest", limit = 200, nearTileId = origin, maxDistance = 8 }
            : new { page = page, biome = "TemperateForest", limit = 200 };
        var sites = Items(await run.CallAsync(client, "setup.sites", siteQuery));
        if (sites.Length == 0 && !profile.Features)
        {
            // Extreme climates need not contain temperate forest. Site validity
            // still comes from the original settlement validator.
            siteQuery = firstColonyTile is int near
                ? new { page = page, limit = 200, nearTileId = near, maxDistance = 8 }
                : new { page = page, limit = 200 };
            sites = Items(await run.CallAsync(client, "setup.sites", siteQuery));
        }
        if (firstColonyTile is int firstTile)
        {
            // Vanilla graph adjacency and the server's geometry estimate can
            // differ. Select a site accepted by both; never bypass registration.
            var geometry = world.Current!.TileGeometry!;
            var source = new WorldTileGeometryDistanceSource(geometry.Layers.Select(l => new WorldTileLayerGeometry(
                l.LayerId, l.AverageTileSize, l.TileCenters.Select(c => new WorldTileCenter(c.Tile, c.X, c.Y, c.Z)))));
            sites = sites.Where(s => distances.TryCalculateDistance(source,
                new WorldTileRef(firstTile, s.Int("layerId")), new WorldTileRef(s.Int("tileId"), s.Int("layerId")), 0) is >= 2).ToArray();
        }
        Check(sites.Length > 0, "No valid temperate settlement site.");
        await run.CallAsync(client, "setup.site", new { page = page, tileId = sites[0].Int("tileId") });
    }
    internal async Task SelectStoryteller(string client, string page)
    {
        var selected = await run.CallAsync(client, "setup.storyteller", new
        {
            page,
            storyteller = profile.Storyteller,
            difficulty = profile.Difficulty,
            baseDifficulty = profile.BaseDifficulty,
            values = profile.CustomValues,
            permadeath = false
        });
        if (client == "A") expectedDifficulty = selected.GetProperty("values");
    }
}
