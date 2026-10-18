using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Admin;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Administration;

internal interface IClientAdminBaselineSource
{
    IReadOnlyList<TrapClassificationScanEntry> ScanTraps();
    List<ModStandardMarketValueDto> ReadMarketValues();
    List<ModStuffMarketValueDto> ReadStuffMarketValues();
    List<ModQualityMarketValueModifierDto> ReadQualityModifiers();
    List<ModPackableBuildingDto> ReadPackableBuildings();
    List<ModBuildingBaselineDto> ReadBuildings();
    List<ModStuffHitPointModifierDto> ReadStuffHitPointModifiers();
    void AppendExtensions(ModSubmitAdminBaselineRequestDto baseline, IReadOnlyList<ModAdminBaselineExtensionRequirementDto>? requirements);
}

// Loaded definitions also exist at the menu. Session ownership is mandatory;
// callers tied to a world add that boundary without imposing it on menu capture.
internal sealed class ClientAdminBaselineCapture
{
    internal sealed record Captured(ClientRequestTracker<object?>.Scope Scope, ClashOfRimClientNetworkContext Context);
    private readonly ClientSessionLifecycle session;
    private readonly ClientRequestTracker requests;
    private readonly IClientAdminBaselineSource source;
    private readonly Action<Action, Action> enqueue;
    internal ClientAdminBaselineCapture(ClientSessionLifecycle session, IClientAdminBaselineSource source, Action<Action, Action> enqueue)
    { this.session = session; requests = new(session, static () => { }); this.source = source; this.enqueue = enqueue; }
    internal Captured Capture() => requests.Read(() => new Captured(requests.Capture(), session.ReadContext()));

    internal async Task<ModSubmitAdminBaselineRequestDto> CaptureAsync(Captured owner,
        IReadOnlyList<ModAdminBaselineExtensionRequirementDto>? requirements, CancellationToken token = default, Func<bool>? current = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ClientSessionWork.Token);
        var cancellationToken = cancellation.Token;
        var frozen = requirements is null ? null : ClientProtocolJson.Copy(requirements.ToList());
        ModSubmitAdminBaselineRequestDto? result = null;
        ExceptionDispatchInfo? failure = null;
        bool consumed = false;
        bool TryApply(Action action)
        {
            bool applied = false;
            requests.TryApply(owner.Scope, () =>
            {
                if (cancellation.IsCancellationRequested || session.IsUnavailable || (current is not null && !current())) return;
                action(); applied = true;
            });
            return applied;
        }
        void CheckCurrent()
        {
            if (!TryApply(static () => { })) throw new OperationCanceledException("The administrator baseline capture ended.", cancellationToken);
        }
        T Read<T>(Func<T> read)
        { CheckCurrent(); var value = read(); CheckCurrent(); return value; }
        ModSubmitAdminBaselineRequestDto Build()
        {
            DateTimeOffset generated = DateTimeOffset.UtcNow;
            var traps = TrapClassificationUploadBuilder.Build(Read(source.ScanTraps), null, generated, owner.Context.UserId);
            var baseline = new ModSubmitAdminBaselineRequestDto { GeneratedAtUtc = generated.ToString("O", CultureInfo.InvariantCulture) };
            baseline.StandardMarketValues.AddRange(Read(source.ReadMarketValues));
            baseline.StuffMarketValues.AddRange(Read(source.ReadStuffMarketValues));
            baseline.QualityMarketValueModifiers.AddRange(Read(source.ReadQualityModifiers));
            baseline.PackableBuildings.AddRange(Read(source.ReadPackableBuildings));
            baseline.Buildings.AddRange(Read(source.ReadBuildings));
            CheckCurrent(); source.AppendExtensions(baseline, frozen); CheckCurrent();
            baseline.StuffHitPointModifiers.AddRange(Read(source.ReadStuffHitPointModifiers));
            baseline.TrapClassifications.AddRange(traps.Entries.Select(entry => new ModTrapClassificationDto
            {
                DefName = entry.DefName, ThingClass = entry.ThingClass, ModPackageId = entry.ModPackageId, ModName = entry.ModName,
                ScanStatus = entry.ScanStatus.ToString(), ScanReason = entry.ScanReason,
                InheritsBuildingTrap = entry.ScanStatus == TrapClassificationScanStatus.ApprovedByInheritance, AdminApproved = entry.AdminApproved
            }));
            // Extension providers may retain references. Publish an owned snapshot of their result.
            return ClientProtocolJson.Copy(baseline);
        }
        void Enqueue(Action action, Action discard) => enqueue(action, () => { consumed = true; discard(); });
        await ClientGamePublication.EnqueueAsync(Enqueue, cancellationToken, TryApply, () =>
        {
            if (consumed) return;
            consumed = true;
            // Report capture errors to its awaiting operation, not the game queue drain.
            try { result = Build(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }).ConfigureAwait(false);
        CheckCurrent(); failure?.Throw();
        return result ?? throw new OperationCanceledException("The administrator baseline capture was discarded.", cancellationToken);
    }
}
