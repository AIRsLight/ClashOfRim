using System;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal interface IClientCompatibilityOverridePresentation
{
    string Text(string key, params (string Name, object? Value)[] args);
    void Diagnose(string message);
}

// Credential-authorized recovery also runs at the menu, before compatible login.
// One admission owns override, local publication and the dependent catalog upload.
internal sealed class ClientCompatibilityOverride
{
    private const string Prefix = "ClashOfRim.Compatibility.Override.";
    private readonly ClientAdminAccess access;
    private readonly IClientCompatibilityManifestSource source;
    private readonly ClientCompatibilityBaseline baseline;
    private readonly ClientAdminBaselineCapture capture;
    private readonly IClientAdminBaselineTransport catalogs;
    private readonly IClientCompatibilityOverrideTransport transport;
    private readonly IClientCompatibilityOverridePresentation presentation;
    private readonly Action<Action, Action> enqueue;
    internal ClientCompatibilityOverride(ClientAdminAccess access, IClientCompatibilityManifestSource source,
        ClientCompatibilityBaseline baseline, ClientAdminBaselineCapture capture, IClientAdminBaselineTransport catalogs,
        IClientCompatibilityOverrideTransport transport, IClientCompatibilityOverridePresentation presentation, Action<Action, Action> enqueue)
    {
        this.access = access; this.source = source; this.baseline = baseline; this.capture = capture;
        this.catalogs = catalogs; this.transport = transport; this.presentation = presentation; this.enqueue = enqueue;
    }
    internal Task StartAsync(Action<string, float>? progress, Action<string, bool> completion)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = access.Capture();
        var ticket = access.BeginCredentials(out string? denial);
        if (ticket is not null) return ExecuteAsync(ticket, progress, completion);
        string text = presentation.Text(denial ?? "ClashOfRim.Sync.StatusBusy");
        access.TryApply(scope, () => completion(text, false));
        return Task.CompletedTask;
    }
    private async Task ExecuteAsync(ClientAdminAccess.Ticket ticket, Action<string, float>? progress, Action<string, bool> completion)
    {
        bool accepted = false, finished = false, sending = false;
        bool Current() => access.IsCurrent(ticket);
        bool Step(Action action) => access.TryApply(ticket, action) && Current();
        string Text(string key, params (string Name, object? Value)[] args) => Current() ? presentation.Text(Prefix + key, args) : "";
        bool Progress(string key, float value)
        {
            string message = "";
            return Step(() => message = Text(key)) && Step(() => progress?.Invoke(message, value));
        }
        void Finish(string message)
        {
            if (finished || !Progress(accepted ? "StageComplete" : "StageFailed", accepted ? 1f : 0.95f)) return;
            Step(() => { finished = true; completion(message, accepted); });
        }
        async Task<bool> Publish(Action action)
        {
            bool consumed = false, applied = false;
            await ClientGamePublication.EnqueueAsync((run, discard) => enqueue(run, () => { consumed = true; discard(); }),
                ticket.Request.Scope.Owner.Token, effect => access.TryApply(ticket, effect), () =>
                {
                    if (consumed) return; consumed = true;
                    action(); applied = true;
                }).ConfigureAwait(false);
            return applied && Current();
        }
        void CompleteFailure(string detail, bool catalog, string? diagnostic = null)
        {
            string message = detail;
            if (catalog) message = Text("Accepted") + " " + Text("AdminBaselineNotUpdated", ("MESSAGE", detail));
            if (!Current()) return;
            if (!Step(() => presentation.Diagnose(diagnostic ?? (catalog ? "Administrator catalogs after override failed: " : "Compatibility override failed: ") + detail))) return;
            Finish(message);
        }
        try
        {
            // Capture account and catalog ownership before manifest/progress callbacks.
            var owner = capture.CaptureCredentials(ticket);
            if (!Progress("StageManifest", 0.12f)) return;
            string? manifest = null;
            if (!Step(() => manifest = source.BuildJson(null))) return;
            if (string.IsNullOrWhiteSpace(manifest)) { Finish(Text("ManifestBuildFailed")); return; }
            if (!Progress("StageUpload", 0.24f)) return;
            sending = true;
            await ClientSessionWork.Run(async () =>
            {
                if (!Current()) return;
                var response = await transport.SubmitAsync(ticket.Context, manifest!, ticket.Request.Scope.Owner.Token).ConfigureAwait(false);
                if (!Current()) return;
                accepted = response.Success && response.Response?.Result?.Accepted == true;
                if (!accepted)
                {
                    string? failure = response.Response?.Result?.Message ?? response.Message;
                    await Publish(() => CompleteFailure(failure ?? Text("Failed"), false)); return;
                }
                string? serverManifest = response.Response!.ServerCompatibilityManifestJson;
                if (!await Publish(() =>
                {
                    baseline.Apply(serverManifest, Current);
                    if (Current()) Progress("StageAdminBaseline", 0.56f);
                }).ConfigureAwait(false)) return;
                var requirements = await catalogs.RequirementsAsync(ticket.Context, ticket.Request.Scope.Owner.Token).ConfigureAwait(false);
                if (!Current()) return;
                if (!requirements.Success || requirements.Response?.Result?.Accepted != true)
                {
                    string? failure = requirements.Response?.Result?.Message ?? requirements.Message;
                    await Publish(() => CompleteFailure(failure ?? Text("AdminBaselineFailed"), true)); return;
                }
                var catalog = await capture.CaptureAsync(owner, requirements.Response.BaselineExtensions,
                    ticket.Request.Scope.Owner.Token, Current).ConfigureAwait(false);
                if (!Current() || !await Publish(() => Progress("StageSubmitAdminBaseline", 0.72f)).ConfigureAwait(false)) return;
                var submitted = await catalogs.SubmitAsync(ticket.Context, catalog, ticket.Request.Scope.Owner.Token).ConfigureAwait(false);
                if (!Current()) return;
                if (!submitted.Success || submitted.Response?.Result?.Accepted != true)
                {
                    string? failure = submitted.Response?.Result?.Message ?? submitted.Message;
                    await Publish(() => CompleteFailure(failure ?? Text("AdminBaselineFailed"), true)); return;
                }
                var result = ClientProtocolJson.Copy(submitted.Response);
                await Publish(() => Finish(Text("Accepted") + " " + Text("AdminBaselineUpdated",
                    ("PRICECOUNT", result.StandardMarketValueCount), ("AUTOCOUNT", result.TrapAutoApprovedCount), ("CANDIDATECOUNT", result.TrapCandidateCount)))).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!Current()) return;
            void Failed() => CompleteFailure(Text("Exception", ("TYPE", ex.GetType().Name)), accepted, "Failed to override server baseline: " + ex);
            if (sending) await Publish(Failed).ConfigureAwait(false); else Failed();
        }
        finally { access.End(ticket); }
    }
}
