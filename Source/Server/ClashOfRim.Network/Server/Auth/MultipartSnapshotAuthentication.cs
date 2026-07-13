using AIRsLight.ClashOfRim.Protocol;
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
    {
        string? token = request.Headers[HeaderName].FirstOrDefault();
        if (!state.AuthTokens.TryGetPrincipal(token, nowUtc, out AuthTokenPrincipal? principal)
            || principal is null)
        {
            return false;
        }

        if (!state.LoginSessions.Refresh(
            principal.UserId,
            principal.ColonyId,
            principal.SessionId,
            nowUtc))
        {
            return false;
        }

        request.HttpContext.Items[PrincipalItemKey] = principal;
        return true;
    }

    public static bool MatchesPrincipal(HttpRequest request, string? userId, string? colonyId)
    {
        return request.HttpContext.Items.TryGetValue(PrincipalItemKey, out object? value)
            && value is AuthTokenPrincipal principal
            && string.Equals(principal.UserId, userId, StringComparison.Ordinal)
            && string.Equals(principal.ColonyId, colonyId, StringComparison.Ordinal);
    }
}
