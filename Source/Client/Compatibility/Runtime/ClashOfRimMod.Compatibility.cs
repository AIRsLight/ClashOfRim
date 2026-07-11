using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Admin;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    private const int ProtocolErrorCodeValidationFailed = 7;

    private static string? BuildCompatibilityManifestJsonForLogin()
    {
        try
        {
            return ClientCompatibilityManifestBuilder.BuildJson();
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to build client manifest: " + ex);
            return null;
        }
    }

    private static string? BuildCompatibilityManifestIdForLogin()
    {
        try
        {
            return ClientCompatibilityManifestBuilder.Build().ManifestId;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to build client manifest id: " + ex);
            return null;
        }
    }

    private static string? BuildCompatibilityManifestSummaryJsonForLogin()
    {
        try
        {
            return ClientCompatibilityManifestBuilder.BuildSummaryJson();
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to build client manifest summary: " + ex);
            return null;
        }
    }

    private static string? BuildCompatibilityManifestJsonForPackages(IReadOnlyCollection<string>? packageIds)
    {
        try
        {
            return ClientCompatibilityManifestBuilder.BuildJsonForPackages(packageIds);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to build requested client manifest package details: " + ex);
            return null;
        }
    }

    private static void LogCompatibilityManifestForServerEntry()
    {
        CompatibilityManifest manifest = ClientCompatibilityManifestBuilder.Build();
        Log.Message(ClientCompatibilityManifestDiagnostics.FormatForServerEntry(manifest));
    }

    internal void CaptureServerCompatibilityManifest(string? manifestJson)
    {
        serverCompatibilityManifestJson = manifestJson ?? string.Empty;
        ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(ReadServerDlcIds(serverCompatibilityManifestJson));
        ModSettingsBaselinePolicy.InvalidateManifestCache();
    }

    private static IReadOnlyList<string>? ReadServerDlcIds(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return null;
        }

        try
        {
            var serializer = new DataContractJsonSerializer(typeof(ServerDlcManifestDto));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
            return (serializer.ReadObject(stream) as ServerDlcManifestDto)?.DlcIds;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to parse server DLC baseline: " + ex);
            return null;
        }
    }

    [DataContract]
    private sealed class ServerDlcManifestDto
    {
        [DataMember(Name = "dlcIds")]
        public List<string> DlcIds { get; set; } = new();
    }

    internal void StartOverrideCompatibilityBaselineFromCurrentClient(
        Action<string, float>? progress,
        Action<string, bool> completion)
    {
        CloseServerEntryProgressWindowNow();
        progress?.Invoke(ClashOfRimText.Key("ClashOfRim.Compatibility.Override.StageManifest"), 0.12f);
        string? manifestJson = BuildCompatibilityManifestJsonForLogin();
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            completion(ClashOfRimText.Key("ClashOfRim.Compatibility.Override.ManifestBuildFailed"), false);
            return;
        }

        progress?.Invoke(ClashOfRimText.Key("ClashOfRim.Compatibility.Override.StageUpload"), 0.24f);
        Task.Run(async () =>
        {
            string message;
            bool accepted = false;
            try
            {
                ClashLog.Message("[ClashOfRim][Compatibility] Override baseline request started.");
                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(15)
                };
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModOverrideCompatibilityBaselineResponseDto> result =
                    await client.OverrideCompatibilityBaselineAsync(manifestJson!);
                accepted = result.Success && (result.Response?.Result?.Accepted ?? false);
                message = accepted
                    ? ClashOfRimText.Key("ClashOfRim.Compatibility.Override.Accepted")
                    : result.Response?.Result?.Message
                    ?? result.Message
                    ?? ClashOfRimText.Key("ClashOfRim.Compatibility.Override.Failed");
                if (accepted)
                {
                    EnqueueClashOfRimMainThreadAction(() =>
                        CaptureServerCompatibilityManifest(result.Response?.ServerCompatibilityManifestJson));
                    EnqueueClashOfRimMainThreadAction(() =>
                        progress?.Invoke(ClashOfRimText.Key("ClashOfRim.Compatibility.Override.StageAdminBaseline"), 0.56f));
                    ClashOfRimClientNetworkResult<ModGetAdminBaselineRequirementsResponseDto> requirementsResult =
                        await client.GetAdminBaselineRequirementsAsync();
                    if (!requirementsResult.Success || requirementsResult.Response?.Result?.Accepted != true)
                    {
                        string requirementMessage = requirementsResult.Response?.Result?.Message
                            ?? requirementsResult.Message
                            ?? ClashOfRimText.Key("ClashOfRim.Compatibility.Override.AdminBaselineFailed");
                        message += " " + ClashOfRimText.Key(
                            "ClashOfRim.Compatibility.Override.AdminBaselineNotUpdated",
                            requirementMessage.Named("MESSAGE"));
                        Log.Warning("[ClashOfRim][Compatibility] Admin baseline requirement fetch after compatibility override failed: "
                            + requirementMessage);
                        EnqueueClashOfRimMainThreadAction(() => completion(message, accepted));
                        return;
                    }

                    ModSubmitAdminBaselineRequestDto adminBaseline = await BuildAdminBaselineOnMainThreadAsync(
                        requirementsResult.Response.BaselineExtensions);
                    EnqueueClashOfRimMainThreadAction(() =>
                        progress?.Invoke(ClashOfRimText.Key("ClashOfRim.Compatibility.Override.StageSubmitAdminBaseline"), 0.72f));
                    ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto> baselineResult =
                        await client.SubmitAdminBaselineAsync(adminBaseline);
                    if (baselineResult.Success && baselineResult.Response?.Result?.Accepted == true)
                    {
                        ModSubmitAdminBaselineResponseDto baseline = baselineResult.Response;
                        message += " " + ClashOfRimText.Key(
                            "ClashOfRim.Compatibility.Override.AdminBaselineUpdated",
                            baseline.StandardMarketValueCount.Named("PRICECOUNT"),
                            baseline.TrapAutoApprovedCount.Named("AUTOCOUNT"),
                            baseline.TrapCandidateCount.Named("CANDIDATECOUNT"));
                    }
                    else
                    {
                        string baselineMessage = baselineResult.Response?.Result?.Message
                            ?? baselineResult.Message
                            ?? ClashOfRimText.Key("ClashOfRim.Compatibility.Override.AdminBaselineFailed");
                        message += " " + ClashOfRimText.Key(
                            "ClashOfRim.Compatibility.Override.AdminBaselineNotUpdated",
                            baselineMessage.Named("MESSAGE"));
                        Log.Warning("[ClashOfRim][Compatibility] Admin baseline submit after compatibility override failed: "
                            + baselineMessage);
                    }
                }

                ClashLog.Message("[ClashOfRim][Compatibility] Override baseline request finished: success="
                    + result.Success
                    + ", accepted="
                    + accepted
                    + ", message="
                    + message);
            }
            catch (Exception ex)
            {
                message = ClashOfRimText.Key("ClashOfRim.Compatibility.Override.Exception", ex.GetType().Name.Named("TYPE"));
                Log.Warning("[ClashOfRim][Compatibility] Failed to override server baseline: " + ex);
            }

            EnqueueClashOfRimMainThreadAction(() =>
            {
                progress?.Invoke(
                    accepted
                        ? ClashOfRimText.Key("ClashOfRim.Compatibility.Override.StageComplete")
                        : ClashOfRimText.Key("ClashOfRim.Compatibility.Override.StageFailed"),
                    accepted ? 1f : 0.95f);
                completion(message, accepted);
            });
        });
    }

    private void ShowCompatibilityMismatchWindow(
        ModLoginResponseDto? response,
        Action? continueAnyway = null,
        Action? cancelContinuation = null,
        string? authoritativeServerGameLanguage = null)
    {
        if (!ShouldShowCompatibilityMismatchWindow(response))
        {
            return;
        }

        response ??= new ModLoginResponseDto();
        if (response.CompatibilityIssues is { Count: > 0 })
        {
            ClashLog.Message("[ClashOfRim][Compatibility] mismatch issues:\n"
                + string.Join(
                    "\n",
                    response.CompatibilityIssues.Select(issue =>
                        "  - severity="
                        + (issue.Severity ?? string.Empty)
                        + ", code="
                        + (issue.Code ?? string.Empty)
                        + ", subject="
                        + (issue.Subject ?? string.Empty)
                        + ", message="
                        + (issue.Message ?? string.Empty))));
        }

        EnqueueClashOfRimMainThreadAction(() =>
        {
            if (Find.WindowStack.WindowOfType<CompatibilityMismatchWindow>() is null)
            {
                Find.WindowStack.Add(new CompatibilityMismatchWindow(
                    this,
                    response,
                    authoritativeServerGameLanguage,
                    continueAnyway,
                    cancelContinuation));
            }
        });
    }

    private void ShowCompatibilityMismatchWindow(
        ModPrepareWorldSessionResponseDto? response,
        Action? continueAnyway = null,
        Action? cancelContinuation = null)
    {
        if (response is null)
        {
            return;
        }

        ShowCompatibilityMismatchWindow(new ModLoginResponseDto
        {
            Result = response.Result,
            ServerCompatibilityManifestJson = response.ServerCompatibilityManifestJson,
            CompatibilityIssues = response.CompatibilityIssues ?? new List<ModCompatibilityIssueDto>(),
            CanOverrideCompatibilityBaseline = response.CanOverrideCompatibilityBaseline
        }, continueAnyway, cancelContinuation, response.WorldConfiguration?.GameLanguage);
    }

    private bool ShouldShowCompatibilityMismatchWindow(ModLoginResponseDto? response)
    {
        if (response is null)
        {
            return false;
        }

        if (languageMismatchAcceptedForCurrentServerEntry
            && CompatibilityLanguageMismatchPolicy.CanContinue(response))
        {
            return false;
        }

        if (response.CompatibilityIssues is { Count: > 0 })
        {
            return response.Result?.Accepted == true
                ? response.CompatibilityIssues.Any(issue => !string.Equals(issue.Severity, "Info", StringComparison.OrdinalIgnoreCase))
                : true;
        }

        if (response.Result?.Accepted == true)
        {
            return false;
        }

        return response.Result?.ErrorCode == ProtocolErrorCodeValidationFailed
            && !string.IsNullOrWhiteSpace(response.ServerCompatibilityManifestJson);
    }

}
