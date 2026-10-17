using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

internal sealed class ClientMercenaryProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly ClashOfRimClientNetworkContext context;
    private readonly Func<string> notConfigured;
    internal ClientMercenaryProtocol(ClientProtocolTransport transport, ClashOfRimClientNetworkContext context, Func<string> notConfigured)
    { this.transport = transport.ForContext(context); this.context = context; this.notConfigured = notConfigured; }

    internal Task<ClashOfRimClientNetworkResult<ModMercenaryHireResponseDto>> HireMercenaryAsync(
        string idempotencyKey,
        long currentGameTicks,
        string skillDefName,
        int skillLevel,
        int durationDays,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryHireResponseDto>();
        }

        var request = new ModHireMercenaryRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            SkillDefName = skillDefName,
            SkillLevel = skillLevel,
            DurationDays = durationDays
        };

        return transport.PostAsync<ModHireMercenaryRequestDto, ModMercenaryHireResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.HireMercenary).Route,
            request,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModMercenaryHireResponseDto>> HireMercenaryWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryContractDto contract,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryHireResponseDto>();
        }

        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.MercenaryHireConfirmation;
        var request = new ModHireMercenaryWithSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            SkillDefName = contract.SkillDefName,
            SkillLevel = contract.SkillLevel,
            DurationDays = contract.DurationDays,
            RequestedContractId = contract.ContractId,
            ExpectedPriceSilver = contract.PriceSilver,
            ExpectedHarmfulSurgeryFineSilver = contract.HarmfulSurgeryFineSilver,
            ExpectedDeathFineSilver = contract.DeathFineSilver,
            ConfirmedSnapshot = confirmedSnapshot
        };

        return transport.PostSnapshotMultipartAsync<ModHireMercenaryWithSnapshotRequestDto, ModMercenaryHireResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.HireMercenaryWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModMercenaryQuoteResponseDto>> QuoteMercenaryAsync(
        long currentGameTicks,
        string skillDefName,
        int skillLevel,
        int durationDays,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryQuoteResponseDto>();
        }

        var request = new ModQuoteMercenaryRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            SkillDefName = skillDefName,
            SkillLevel = skillLevel,
            DurationDays = durationDays
        };

        return transport.PostAsync<ModQuoteMercenaryRequestDto, ModMercenaryQuoteResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.QuoteMercenary).Route,
            request,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModMercenaryGuardQuoteResponseDto>> QuoteMercenaryGuardAsync(
        long currentGameTicks,
        string tier,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryGuardQuoteResponseDto>();
        }

        var request = new ModQuoteMercenaryGuardRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            Tier = tier
        };

        return transport.PostAsync<ModQuoteMercenaryGuardRequestDto, ModMercenaryGuardQuoteResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.QuoteMercenaryGuard).Route,
            request,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModMercenaryGuardHireResponseDto>> HireMercenaryGuardWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        ModMercenaryGuardContractDto contract,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryGuardHireResponseDto>();
        }

        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.MercenaryGuardHireConfirmation;
        var request = new ModHireMercenaryGuardWithSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            RequestedContractId = contract.ContractId,
            Tier = contract.Tier,
            ExpectedPriceSilver = contract.PriceSilver,
            ExpectedPointRatio = contract.PointRatio,
            ConfirmedSnapshot = confirmedSnapshot
        };

        return transport.PostSnapshotMultipartAsync<ModHireMercenaryGuardWithSnapshotRequestDto, ModMercenaryGuardHireResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.HireMercenaryGuardWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>> ReportMercenaryIncidentAsync(
        string idempotencyKey,
        long currentGameTicks,
        string contractId,
        string incidentKind,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModMercenaryIncidentResponseDto>();
        }

        var request = new ModReportMercenaryIncidentRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            ContractId = contractId,
            IncidentKind = incidentKind
        };

        return transport.PostAsync<ModReportMercenaryIncidentRequestDto, ModMercenaryIncidentResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ReportMercenaryIncident).Route,
            request,
            cancellationToken);
    }

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() => Task.FromResult(
        ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfigured()));
}
