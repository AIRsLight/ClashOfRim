using AIRsLight.ClashOfRim.Protocol;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public interface ISteamAuthTicketValidator
{
    SteamAuthTicketValidationResult Validate(string userId, string? steamAuthTicket);
}

public sealed class DevelopmentSteamAuthTicketValidator : ISteamAuthTicketValidator
{
    public SteamAuthTicketValidationResult Validate(string userId, string? steamAuthTicket)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return SteamAuthTicketValidationResult.Reject(ServerLocalization.Text("Steam.MissingUser"));
        }

        return SteamAuthTicketValidationResult.Accept(userId.Trim());
    }
}

public sealed class SteamWebApiAuthTicketValidator : ISteamAuthTicketValidator
{
    private readonly string webApiKey;
    private readonly uint appId;
    private readonly HttpClient httpClient;

    public SteamWebApiAuthTicketValidator(string webApiKey, uint appId, HttpClient? httpClient = null)
    {
        this.webApiKey = string.IsNullOrWhiteSpace(webApiKey)
            ? throw new ArgumentException("Steam Web API key is required.", nameof(webApiKey))
            : webApiKey.Trim();
        this.appId = appId == 0 ? 294100U : appId;
        this.httpClient = httpClient ?? new HttpClient();
    }

    public SteamAuthTicketValidationResult Validate(string userId, string? steamAuthTicket)
    {
        if (string.IsNullOrWhiteSpace(steamAuthTicket))
        {
            return SteamAuthTicketValidationResult.Reject(ServerLocalization.Text("Steam.MissingTicket"));
        }

        try
        {
            string ticket = steamAuthTicket.Trim();
            Uri authenticateUri = BuildAuthenticateUserTicketUri(ticket);
            using HttpResponseMessage response = httpClient.GetAsync(authenticateUri).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return SteamAuthTicketValidationResult.Reject(ServerLocalization.Text("Steam.Failed"));
            }

            using Stream stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement parameters = document.RootElement
                .GetProperty("response")
                .GetProperty("params");
            string? result = parameters.TryGetProperty("result", out JsonElement resultElement)
                ? resultElement.GetString()
                : null;
            string? steamId = parameters.TryGetProperty("steamid", out JsonElement steamIdElement)
                ? steamIdElement.GetString()
                : null;
            if (!string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(steamId))
            {
                return SteamAuthTicketValidationResult.Reject(ServerLocalization.Text("Steam.Failed"));
            }

            string? displayName = TryGetPersonaName(steamId!);
            return SteamAuthTicketValidationResult.Accept(steamId!.Trim(), displayName);
        }
        catch (Exception)
        {
            return SteamAuthTicketValidationResult.Reject(ServerLocalization.Text("Steam.Failed"));
        }
    }

    private Uri BuildAuthenticateUserTicketUri(string ticket)
    {
        return new Uri(
            "https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/"
            + "?key=" + Uri.EscapeDataString(webApiKey)
            + "&appid=" + appId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&ticket=" + Uri.EscapeDataString(ticket));
    }

    private string? TryGetPersonaName(string steamId)
    {
        try
        {
            Uri uri = new(
                "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/"
                + "?key=" + Uri.EscapeDataString(webApiKey)
                + "&steamids=" + Uri.EscapeDataString(steamId));
            using HttpResponseMessage response = httpClient.GetAsync(uri).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using Stream stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement players = document.RootElement.GetProperty("response").GetProperty("players");
            if (players.ValueKind != JsonValueKind.Array || players.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement player = players[0];
            return player.TryGetProperty("personaname", out JsonElement nameElement)
                ? nameElement.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed record SteamAuthTicketValidationResult(
    bool Accepted,
    string? SteamId,
    string? DisplayName,
    string? Message)
{
    public static SteamAuthTicketValidationResult Accept(string steamId, string? displayName = null)
    {
        return new SteamAuthTicketValidationResult(true, steamId, displayName, null);
    }

    public static SteamAuthTicketValidationResult Reject(string message)
    {
        return new SteamAuthTicketValidationResult(false, null, null, message);
    }
}
