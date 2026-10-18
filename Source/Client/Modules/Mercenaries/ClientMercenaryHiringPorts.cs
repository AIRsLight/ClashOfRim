using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

internal sealed record ClientMercenarySelection(string Skill, int Level, int Days, string? GuardTier = null)
{
    internal bool IsGuard => GuardTier is not null;
    internal static string NormalizeGuardTier(string? tier) => tier is "Skilled" or "Master" ? tier : "Apprentice";
}
internal sealed record ClientMercenaryOffer(ModProtocolResponseDto? Result, ModBankStatusResponseDto? BankStatus,
    int Price, int SurgeryFine = 0, int DeathFine = 0, string Tier = "", float PointRatio = 0);
internal sealed record ClientMercenaryCommand(string Key, string ContractId, ClientMercenarySelection Selection,
    ClientMercenaryTerms Terms, long GameTicks, long ExpiresAt)
{
    internal ModMercenaryContractDto Contract() => new() { ContractId = ContractId, SkillDefName = Selection.Skill,
        SkillLevel = Selection.Level, DurationDays = Selection.Days, PriceSilver = Terms.Price,
        HarmfulSurgeryFineSilver = Terms.SurgeryFine, DeathFineSilver = Terms.DeathFine,
        CreatedAtGameTicks = GameTicks, ExpiresAtGameTicks = ExpiresAt };
    internal ModMercenaryGuardContractDto GuardContract() => new() { ContractId = ContractId, Tier = Terms.Tier,
        PriceSilver = Terms.Price, PointRatio = Terms.PointRatio, CreatedAtGameTicks = GameTicks };
}
internal sealed record ClientMercenaryTerms(int Price, int SurgeryFine, int DeathFine, string Tier, float PointRatio);
internal sealed record ClientMercenaryHireReply(ModProtocolResponseDto? Result, ModBankStatusResponseDto? BankStatus,
    string? ContractId, string? AppliedSnapshotId, string? NextLineageToken);
internal enum ClientMercenaryPreparation { Prepared, AwaitingArrival, Failed }
internal sealed record ClientMercenaryLocalResult(ClientMercenaryPreparation State, string Message);

internal interface IClientMercenaryRuntime { IClientMercenarySource Capture(); }
internal interface IClientMercenarySource
{
    long GameTicks { get; }
    int TicksPerDay { get; }
    string? Validate(int price);
    ClientMercenaryLocalResult Prepare(ClientMercenaryCommand command, Action mutationStarting);
    // Continue only a known, not-yet-started arrival using the original pawn/map.
    ClientMercenaryLocalResult ResumeArrival();
}
internal interface IClientMercenaryHiringTransport
{
    Task<ClashOfRimClientNetworkResult<ClientMercenaryOffer>> QuoteAsync(ClashOfRimClientNetworkContext context,
        ClientMercenarySelection selection, long ticks, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ClientMercenaryHireReply>> SubmitAsync(ClashOfRimClientNetworkContext context,
        ClientMercenaryCommand command, ModSnapshotPackageMetadataDto package, byte[] payload, CancellationToken token);
}
internal interface IClientMercenaryHiringPresentation : IClientMercenaryPresentation
{
    string Idle { get; }
    string BaseSnapshotChanged { get; }
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    void Message(string message, bool rejected);
}
