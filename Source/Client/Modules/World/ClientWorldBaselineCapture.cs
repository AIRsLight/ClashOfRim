using System;
using System.Threading;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

// Owns the capture sequence and its validation; the bound runtime supplies engine
// data, while the existing submission module owns immutable network input.
internal sealed class ClientWorldBaselineCapture
{
    private readonly ClientWorldBaselineSubmission submission;
    private readonly ClientStorytellerConfiguration storyteller;
    private int capturing;
    internal ClientWorldBaselineCapture(ClientWorldBaselineSubmission submission, ClientStorytellerConfiguration storyteller)
    { this.submission = submission; this.storyteller = storyteller; }

    internal Result Capture(ClientWorldBaselineSubmission.Captured capture, ModColonyAppearanceDto? appearance, bool storytellerOnly = false, Func<bool>? current = null)
    {
        if (!submission.IsCurrent(capture) || current?.Invoke() == false || Interlocked.CompareExchange(ref capturing, 1, 0) != 0) return Result.Empty;
        string? language = null; bool hasLanguage = false;
        bool Current() => submission.IsCurrent(capture) && current?.Invoke() != false && (!hasLanguage || ClientWorldFeatureLabels.SameLanguage(language, capture.Scope.Boundary.Language));
        try
        {
            var source = capture.Scope.Boundary;
            var context = capture.Context;
            var configuration = source.ReadWorld(context.UserId, context.ColonyId, appearance is null ? null : ClientProtocolJson.Copy(appearance));
            language = configuration.GameLanguage; hasLanguage = true;
            if (!Current()) return Result.Empty;
            configuration.WorldConfigurationId = "world:" + Guid.NewGuid().ToString("N");
            configuration.ConfiguredByUserId = context.UserId;
            configuration.ConfiguredByColonyId = context.ColonyId;
            configuration.ConfiguredAtUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var baseline = storyteller.Capture();
            if (!Current()) return Result.Empty;
            if (baseline.CaptureFailure is not null) return new(null, null, new("StorytellerValues", null, baseline.CaptureFailure));
            configuration.StorytellerDefName = baseline.Storyteller;
            configuration.DifficultyDefName = baseline.Difficulty;
            configuration.DifficultyValuesXml = baseline.ValuesXml;
            configuration.Extensions.AddRange(source.ReadExtensions(context.UserId, context.ColonyId, configuration.WorldConfigurationId));
            if (!Current()) return Result.Empty;
            var failure = Validate(configuration, storytellerOnly);
            if (failure is not null) return new(null, null, failure);
            byte[]? payload = source.CaptureSubstrate(configuration.TileGeometry, out string? substrateFailure);
            if (!Current()) return Result.Empty;
            if (payload is null) return new(null, null, new("WorldSubstrate", null, substrateFailure ?? "Unknown"));
            var prepared = submission.Prepare(capture, configuration, payload);
            return prepared is null ? Result.Empty : new(configuration, prepared, null);
        }
        catch (Exception) when (!Current()) { return Result.Empty; }
        finally { Volatile.Write(ref capturing, 0); }
    }
    private static Failure? Validate(ModWorldConfigurationDto configuration, bool storytellerOnly)
    {
        if (storytellerOnly)
            return ClientStorytellerBaseline.From(configuration).HasDefinitions ? null
                : new("MissingStoryteller", null, "storyteller or difficulty is empty");
        string? missing = string.IsNullOrWhiteSpace(configuration.SeedString) ? "Seed"
            : !ClientWorldGenerationInput.TryCoverage(configuration.PlanetCoverage, out _) ? "Coverage"
            : configuration.FactionDefNames.Count == 0 ? "Factions"
            : configuration.Features.Count == 0 ? "Features" : null;
        return missing is null ? null : new("Missing" + missing, "ClashOfRim.WorldBaseline.FailureMissing" + missing, null);
    }
    internal sealed record Failure(string Code, string? MessageKey, string? Detail);
    internal sealed record Result(ModWorldConfigurationDto? Configuration, ClientWorldBaselineSubmission.Prepared? Prepared, Failure? Error)
    { internal static readonly Result Empty = new(null, null, null); }
}
