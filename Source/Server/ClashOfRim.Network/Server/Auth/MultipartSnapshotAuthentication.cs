using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static class MultipartSnapshotAuthentication
{
    private static readonly object PrincipalItemKey = new();
    private static readonly HashSet<string> ProtectedRoutes = new(
        new[]
        {
            ProtocolMessageKind.UploadSnapshot,
            ProtocolMessageKind.ConfirmEventApplication,
            ProtocolMessageKind.ConfirmEventApplications,
            ProtocolMessageKind.CreateGiftWithSnapshot,
            ProtocolMessageKind.CreateTradeOrderWithSnapshot,
            ProtocolMessageKind.FulfillTradeOrderWithSnapshot,
            ProtocolMessageKind.PurchaseServerShopListingWithSnapshot,
            ProtocolMessageKind.CreateRaidWithSnapshot,
            ProtocolMessageKind.CreateSupportPawnWithSnapshot,
            ProtocolMessageKind.UploadWorldSubstrate,
            ProtocolMessageKind.StorePawnPackagePreview,
            ProtocolMessageKind.GetPawnPackagePreview,
            ProtocolMessageKind.CreateBankLoanWithSnapshot,
            ProtocolMessageKind.RepayBankLoanWithSnapshot,
            ProtocolMessageKind.RepayBankDebtWithSnapshot,
            ProtocolMessageKind.HireMercenaryWithSnapshot,
            ProtocolMessageKind.HireMercenaryGuardWithSnapshot
        }.Select(kind => ProtocolContractManifest.Find(kind).Route),
        StringComparer.OrdinalIgnoreCase);

    public const string HeaderName = MultipartSnapshotTransport.AuthenticationHeaderName;

    public static bool IsProtectedRoute(PathString path)
    {
        return path.HasValue && ProtectedRoutes.Contains(path.Value!);
    }

    public static bool TryAuthorize(
        HttpRequest request,
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc)
        => Authorize(request, state, nowUtc) is null;

    public static ProtocolResponse? Authorize(HttpRequest request, ClashOfRimNetworkState state, DateTimeOffset nowUtc)
    {
        string? token = request.Headers[HeaderName].FirstOrDefault();
        if (state.SessionPrincipals.Authorize(token, nowUtc, out AuthTokenPrincipal? principal) is { } rejection)
        {
            request.HttpContext.Items.Remove(PrincipalItemKey);
            return rejection;
        }

        request.HttpContext.Items[PrincipalItemKey] = principal;
        return null;
    }

    public static bool MatchesPrincipal(HttpRequest request, string? userId, string? colonyId)
    {
        return Principal(request) is { } principal && SessionPrincipalAuthorization.Matches(principal, userId, colonyId);
    }

    internal static AuthTokenPrincipal? Principal(HttpRequest request) =>
        request.HttpContext.Items.TryGetValue(PrincipalItemKey, out object? value) ? value as AuthTokenPrincipal : null;
}
