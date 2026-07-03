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
    private readonly IKeyedJsonRecordStore? structuredPersistence;
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly Dictionary<string, OfflineAccountRecord> accounts = new(StringComparer.Ordinal);

    public OfflineAccountRegistry()
        : this((IJsonPersistenceSlot?)null)
    {
    }

    internal OfflineAccountRegistry(IJsonPersistenceSlot? persistence)
        : this(null, persistence)
    {
    }

    internal OfflineAccountRegistry(
        IKeyedJsonRecordStore? structuredPersistence,
        IJsonPersistenceSlot? legacyPersistence)
    {
        this.structuredPersistence = structuredPersistence;
        this.legacyPersistence = legacyPersistence;
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
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        bool importedLegacy = !hasStructured && LoadLegacyReadOnly();
        if (importedLegacy && structuredPersistence is not null)
        {
            Save();
        }
    }

    private void LoadStructured()
    {
        if (structuredPersistence is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> pair in structuredPersistence.ReadAll())
        {
            try
            {
                OfflineAccountRecord? account = JsonSerializer.Deserialize<OfflineAccountRecord>(pair.Value, JsonOptions);
                RegisterLoadedAccount(account, overwrite: true);
            }
            catch (JsonException)
            {
            }
        }
    }

    private bool LoadLegacyReadOnly()
    {
        string? json = legacyPersistence?.Read();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            OfflineAccountPersistence? persisted = JsonSerializer.Deserialize<OfflineAccountPersistence>(json, JsonOptions);
            bool imported = false;
            foreach (OfflineAccountRecord account in persisted?.Accounts ?? Array.Empty<OfflineAccountRecord>())
            {
                imported |= RegisterLoadedAccount(account, overwrite: false);
            }

            return imported;
        }
        catch
        {
            return false;
        }
    }

    private void Save()
    {
        if (structuredPersistence is not null)
        {
            structuredPersistence.ReplaceAll(accounts.ToDictionary(
                pair => AccountRowKey(pair.Key),
                pair => JsonSerializer.Serialize(pair.Value, JsonOptions),
                StringComparer.Ordinal));
            return;
        }

        legacyPersistence?.Write(JsonSerializer.Serialize(
            new OfflineAccountPersistence(accounts.Values.OrderBy(account => account.UserId, StringComparer.Ordinal).ToList()),
            JsonOptions));
    }

    private bool RegisterLoadedAccount(OfflineAccountRecord? account, bool overwrite)
    {
        if (account is null)
        {
            return false;
        }

        string userId = NormalizeUserId(account.UserId);
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(account.PasswordHash)
            || string.IsNullOrWhiteSpace(account.PasswordSalt))
        {
            return false;
        }

        if (accounts.ContainsKey(userId) && !overwrite)
        {
            return false;
        }

        accounts[userId] = account with { UserId = userId };
        return true;
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

    private static string AccountRowKey(string userId)
    {
        return "account:" + userId;
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
