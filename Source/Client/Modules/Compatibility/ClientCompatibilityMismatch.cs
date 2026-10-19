using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal enum ClientCompatibilityRepair { Manifest, ConfigOverlay, LocalConfigs }
internal interface IClientCompatibilityRepairRuntime
{
    string Apply(ClientCompatibilityRepair repair, string manifestJson);
}

// Captured before queuing UI work. Recovery actions belong to the warning's
// original session, Game and credential, even when no login has been accepted yet.
internal sealed class ClientCompatibilityMismatch
{
    private readonly ClientRequestTracker<Boundary> lifetime;
    private readonly ClientRequestTracker<Boundary>.Scope owner;
    private readonly ClientAdminAccess access;
    private readonly ClientCompatibilityOverride operation;
    private readonly IClientCompatibilityManifestSource manifests;
    private readonly IClientCompatibilityModOrderSource order;
    private readonly IClientCompatibilityReportText text;
    private readonly IClientCompatibilityRepairRuntime repair;
    private readonly ModLoginResponseDto response;
    private readonly string? language;
    private readonly Action? accept, cancel;
    private bool closed, handled, applying;
    internal ClientCompatibilityReport? Report { get; private set; }
    internal string Status { get; private set; } = "";
    internal string IssueSummary => string.Join("\n", response.CompatibilityIssues?.Select(issue =>
        "  - severity=" + issue.Severity + ", code=" + issue.Code + ", subject=" + issue.Subject + ", message=" + issue.Message) ?? Array.Empty<string>());

    internal ClientCompatibilityMismatch(ClientSessionLifecycle session, IClientSnapshotGame game, ClientAdminAccess access,
        ClientCompatibilityOverride operation, IClientCompatibilityManifestSource manifests, IClientCompatibilityModOrderSource order,
        IClientCompatibilityReportText text, IClientCompatibilityRepairRuntime repair, ModLoginResponseDto response, string? language,
        Action? accept = null, Action? cancel = null, Func<bool>? current = null)
    {
        this.access = access; this.operation = operation; this.manifests = manifests; this.order = order;
        this.text = text; this.repair = repair; this.language = language; this.accept = accept; this.cancel = cancel;
        this.response = ClientProtocolJson.Copy(response);
        lifetime = new(session, () =>
        {
            var context = session.ReadContext();
            return new(game.BindCurrentGame(), context.OfflinePassword, context.SteamAuthTicket, session.IsUnavailable);
        }, boundary =>
        {
            if (current?.Invoke() == false || !boundary.Game.IsCurrent) return false;
            var context = session.ReadContext();
            return boundary.Password == context.OfflinePassword && boundary.Steam == context.SteamAuthTicket
                && boundary.Unavailable == session.IsUnavailable;
        }, static () => { });
        owner = lifetime.Capture();
    }
    internal bool IsCurrent
    {
        get
        {
            if (closed) return false;
            bool valid = false;
            lifetime.TryApply(owner, () => valid = !closed);
            if (!valid) Close();
            return valid;
        }
    }
    internal bool Open()
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!IsCurrent) return false;
        if (Report is not null) return true;
        try
        {
            string? local = manifests.BuildJson(null);
            if (!IsCurrent) return false;
            var report = new ClientCompatibilityReport(response, local, language, text);
            if (!IsCurrent) return false;
            Report = report; return true;
        }
        catch { Close(); throw; }
    }
    internal bool CanContinue => Report?.LanguageOnly == true && accept is not null && IsCurrent;
    internal bool CanOverride => Report?.CanOverride == true && !CanContinue && IsCurrent;
    internal bool CanApplyManifest => Report?.CanApplyManifest == true && !CanContinue && IsCurrent;
    internal bool CanApplyConfig => Report?.CanApplyConfig == true && IsCurrent;
    internal void Close()
    {
        using var p = ClientSessionWork.EnterPublication();
        closed = true;
        if (handled) return;
        handled = true; cancel?.Invoke();
    }
    internal bool Continue(Action closeWindow)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!CanContinue) return false;
        handled = true; closed = true;
        try { closeWindow(); }
        catch { cancel?.Invoke(); throw; }
        // Closing a window can run engine callbacks. If they replace this owner,
        // release the original continuation instead of accepting another session.
        if (!lifetime.TryApply(owner, () => accept!())) cancel?.Invoke();
        return true;
    }
    internal bool Apply(ClientCompatibilityRepair kind)
    {
        using var p = ClientSessionWork.EnterPublication();
        bool allowed = kind == ClientCompatibilityRepair.Manifest ? CanApplyManifest
            : kind is ClientCompatibilityRepair.ConfigOverlay or ClientCompatibilityRepair.LocalConfigs && CanApplyConfig;
        if (!allowed || applying) return false;
        applying = true;
        try
        {
            string status = repair.Apply(kind, Report!.ManifestJson);
            if (IsCurrent) Status = status;
            return true;
        }
        finally { applying = false; }
    }
    internal ClientCompatibilityOverrideIntent? PrepareOverride()
        => ClientCompatibilityOverrideIntent.Prepare(order, operation, Report?.ServerModOrder,
            TryOverride, () => CanOverride && !access.Busy);
    private bool TryOverride(Action action)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!CanOverride || access.Busy) return false;
        return lifetime.TryApply(owner, action);
    }
    private sealed record Boundary(IClientSnapshotGameSession Game, string? Password, string? Steam, bool Unavailable);
}
