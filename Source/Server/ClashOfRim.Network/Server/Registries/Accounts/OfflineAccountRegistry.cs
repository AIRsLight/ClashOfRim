using System.Security.Cryptography;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class OfflineAccountRegistry
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 120_000;
    public const string MissingUserKey = "OfflineAuth.MissingUser";
    public const string InvalidPasswordKey = "OfflineAuth.InvalidPassword";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object gate = new();
    private readonly IJsonPersistenceSlot? persistence;
    private readonly Dictionary<string, OfflineAccountRecord> accounts = new(StringComparer.Ordinal);

    public OfflineAccountRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal OfflineAccountRegistry(IJsonPersistenceSlot? persistence)
    {
        this.persistence = persistence;
        Load();
    }

    public OfflineAccountAuthenticationResult Authenticate(
        string userId,
        string? password,
        DateTimeOffset nowUtc)
    {
        string normalizedUserId = NormalizeUserId(userId);
        if (string.IsNullOrWhiteSpace(normalizedUserId))
        {
            return OfflineAccountAuthenticationResult.Reject(MissingUserKey);
        }

        lock (gate)
        {
            if (!accounts.TryGetValue(normalizedUserId, out OfflineAccountRecord? account))
            {
                account = CreateRecord(
                    normalizedUserId,
                    password ?? string.Empty,
                    nowUtc);
                accounts[normalizedUserId] = account;
                Save();
            }

            string suppliedPassword = password ?? string.Empty;
            if (!VerifyPassword(suppliedPassword, account))
            {
                return OfflineAccountAuthenticationResult.Reject(InvalidPasswordKey);
            }

            return OfflineAccountAuthenticationResult.Accept(normalizedUserId, account.DisplayName);
        }
    }

    public bool ChangePassword(string userId, string? currentPassword, string? newPassword, DateTimeOffset nowUtc, out string failure)
    {
        string normalizedUserId = NormalizeUserId(userId);
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUserId))
        {
            failure = MissingUserKey;
            return false;
        }

        lock (gate)
        {
            if (!accounts.TryGetValue(normalizedUserId, out OfflineAccountRecord? account))
            {
                account = CreateRecord(normalizedUserId, password: string.Empty, nowUtc);
                accounts[normalizedUserId] = account;
            }

            if (!VerifyPassword(currentPassword ?? string.Empty, account))
            {
                failure = InvalidPasswordKey;
                return false;
            }

            accounts[normalizedUserId] = CreateRecord(
                normalizedUserId,
                newPassword ?? string.Empty,
                account.CreatedAtUtc,
                account.DisplayName,
                nowUtc);
            Save();
            return true;
        }
    }

    public bool ResetPassword(string userId, string? newPassword, DateTimeOffset nowUtc, out string failure)
    {
        string normalizedUserId = NormalizeUserId(userId);
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUserId))
        {
            failure = MissingUserKey;
            return false;
        }

        lock (gate)
        {
            DateTimeOffset createdAtUtc = accounts.TryGetValue(normalizedUserId, out OfflineAccountRecord? account)
                ? account.CreatedAtUtc
                : nowUtc;
            string? displayName = account?.DisplayName;
            accounts[normalizedUserId] = CreateRecord(
                normalizedUserId,
                newPassword ?? string.Empty,
                createdAtUtc,
                displayName,
                nowUtc);
            Save();
            return true;
        }
    }

    private void Load()
    {
        string? json = persistence?.Read();
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            OfflineAccountPersistence? persisted = JsonSerializer.Deserialize<OfflineAccountPersistence>(json, JsonOptions);
            foreach (OfflineAccountRecord account in persisted?.Accounts ?? Array.Empty<OfflineAccountRecord>())
            {
                string userId = NormalizeUserId(account.UserId);
                if (!string.IsNullOrWhiteSpace(userId)
                    && !string.IsNullOrWhiteSpace(account.PasswordHash)
                    && !string.IsNullOrWhiteSpace(account.PasswordSalt))
                {
                    accounts[userId] = account with { UserId = userId };
                }
            }
        }
        catch
        {
            accounts.Clear();
        }
    }

    private void Save()
    {
        persistence?.Write(JsonSerializer.Serialize(
            new OfflineAccountPersistence(accounts.Values.OrderBy(account => account.UserId, StringComparer.Ordinal).ToList()),
            JsonOptions));
    }

    private static OfflineAccountRecord CreateRecord(
        string userId,
        string password,
        DateTimeOffset createdAtUtc,
        string? displayName = null,
        DateTimeOffset? updatedAtUtc = null)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        return new OfflineAccountRecord(
            userId,
            displayName,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash),
            Iterations,
            createdAtUtc,
            updatedAtUtc ?? createdAtUtc);
    }

    private static bool VerifyPassword(string password, OfflineAccountRecord account)
    {
        try
        {
            byte[] salt = Convert.FromBase64String(account.PasswordSalt);
            byte[] expected = Convert.FromBase64String(account.PasswordHash);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Math.Max(1, account.Iterations),
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeUserId(string? userId)
    {
        return (userId ?? string.Empty).Trim();
    }

    private sealed record OfflineAccountPersistence(IReadOnlyList<OfflineAccountRecord> Accounts);

    private sealed record OfflineAccountRecord(
        string UserId,
        string? DisplayName,
        string PasswordSalt,
        string PasswordHash,
        int Iterations,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);
}

public sealed record OfflineAccountAuthenticationResult(
    bool Accepted,
    string? UserId,
    string? DisplayName,
    string? Message)
{
    public static OfflineAccountAuthenticationResult Accept(string userId, string? displayName = null)
    {
        return new OfflineAccountAuthenticationResult(true, userId, displayName, null);
    }

    public static OfflineAccountAuthenticationResult Reject(string message)
    {
        return new OfflineAccountAuthenticationResult(false, null, null, message);
    }
}
