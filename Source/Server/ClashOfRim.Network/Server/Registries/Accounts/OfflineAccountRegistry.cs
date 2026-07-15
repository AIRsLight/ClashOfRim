using System.Security.Cryptography;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public sealed class OfflineAccountRegistry
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 120_000;
    private const int MaximumFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public const string MissingUserKey = "OfflineAuth.MissingUser";
    public const string InvalidPasswordKey = "OfflineAuth.InvalidPassword";
    public const string RateLimitedKey = "OfflineAuth.RateLimited";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object gate = new();
    private readonly IKeyedJsonRecordStore? structuredPersistence;
    private readonly IJsonPersistenceSlot? legacyPersistence;
    private readonly Dictionary<string, OfflineAccountRecord> accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthenticationFailureState> authenticationFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> quarantinedUserIds = new(StringComparer.Ordinal);

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
        DateTimeOffset nowUtc,
        bool createIfMissing = false)
    {
        string normalizedUserId = NormalizeUserId(userId);
        if (string.IsNullOrWhiteSpace(normalizedUserId))
        {
            return OfflineAccountAuthenticationResult.Reject(MissingUserKey);
        }

        string suppliedPassword = password ?? string.Empty;
        lock (gate)
        {
            if (quarantinedUserIds.Contains(normalizedUserId))
            {
                return OfflineAccountAuthenticationResult.Reject(InvalidPasswordKey);
            }

            if (IsLockedOut(normalizedUserId, nowUtc))
            {
                return OfflineAccountAuthenticationResult.Reject(RateLimitedKey);
            }

            if (!accounts.TryGetValue(normalizedUserId, out OfflineAccountRecord? account))
            {
                if (!createIfMissing)
                {
                    return OfflineAccountAuthenticationResult.Reject(MissingUserKey);
                }

                account = CreateRecord(
                    normalizedUserId,
                    suppliedPassword,
                    nowUtc);
                PersistAccount(account);
                accounts[normalizedUserId] = account;
            }

            if (!VerifyPassword(suppliedPassword, account))
            {
                RecordAuthenticationFailure(normalizedUserId, nowUtc);
                return OfflineAccountAuthenticationResult.Reject(InvalidPasswordKey);
            }

            authenticationFailures.Remove(normalizedUserId);
            return OfflineAccountAuthenticationResult.Accept(normalizedUserId, account.DisplayName);
        }
    }

    public bool ChangePassword(string userId, string? currentPassword, string? newPassword, DateTimeOffset nowUtc, out string failure)
    {
        string normalizedUserId = NormalizeUserId(userId);
        string replacementPassword = newPassword ?? string.Empty;
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

            OfflineAccountRecord replacement = CreateRecord(
                normalizedUserId,
                replacementPassword,
                account.CreatedAtUtc,
                account.DisplayName,
                nowUtc);
            PersistAccount(replacement);
            accounts[normalizedUserId] = replacement;
            return true;
        }
    }

    public bool ResetPassword(string userId, string? newPassword, DateTimeOffset nowUtc, out string failure)
    {
        string normalizedUserId = NormalizeUserId(userId);
        string replacementPassword = newPassword ?? string.Empty;
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
            OfflineAccountRecord replacement = CreateRecord(
                normalizedUserId,
                replacementPassword,
                createdAtUtc,
                displayName,
                nowUtc);
            PersistAccount(replacement);
            accounts[normalizedUserId] = replacement;
            quarantinedUserIds.Remove(normalizedUserId);
            return true;
        }
    }

    private void Load()
    {
        bool hasStructured = structuredPersistence?.IsInitialized() == true;
        LoadStructured();
        bool importedLegacy = !hasStructured
            && (structuredPersistence is null || LegacyStructuredImportScope.IsActive)
            && LoadLegacyReadOnly();
        if (importedLegacy && structuredPersistence is not null)
        {
            structuredPersistence.ReplaceAllForImport(accounts.ToDictionary(
                pair => AccountRowKey(pair.Key),
                pair => JsonSerializer.Serialize(pair.Value, JsonOptions),
                StringComparer.Ordinal));
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
                if (!RegisterLoadedAccount(account, overwrite: true))
                {
                    QuarantineStructuredAccount(
                        pair.Key,
                        new InvalidDataException("The account record is missing required identity or password fields."));
                }
            }
            catch (JsonException ex)
            {
                QuarantineStructuredAccount(pair.Key, ex);
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
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            RegistryPersistenceDiagnostics.ReportInvalidRecord("offline-accounts", "<legacy>", ex);
            return false;
        }
    }

    private void QuarantineStructuredAccount(string rowKey, Exception exception)
    {
        RegistryPersistenceDiagnostics.ReportInvalidRecord("offline-accounts", rowKey, exception);
        const string prefix = "account:";
        if (!rowKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return;
        }

        string userId = NormalizeUserId(rowKey.Substring(prefix.Length));
        if (!string.IsNullOrWhiteSpace(userId))
        {
            quarantinedUserIds.Add(userId);
        }
    }

    private void PersistAccount(OfflineAccountRecord account)
    {
        if (structuredPersistence is not null)
        {
            structuredPersistence.ApplyBatch(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AccountRowKey(account.UserId)] = JsonSerializer.Serialize(account, JsonOptions)
                },
                Array.Empty<string>());
            return;
        }

        legacyPersistence?.Write(JsonSerializer.Serialize(
            new OfflineAccountPersistence(accounts.Values
                .Where(existing => !string.Equals(existing.UserId, account.UserId, StringComparison.Ordinal))
                .Append(account)
                .OrderBy(existing => existing.UserId, StringComparer.Ordinal)
                .ToList()),
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

    private bool IsLockedOut(string userId, DateTimeOffset nowUtc)
    {
        if (!authenticationFailures.TryGetValue(userId, out AuthenticationFailureState? state))
        {
            return false;
        }

        if (state.LockedUntilUtc is DateTimeOffset lockedUntilUtc && lockedUntilUtc > nowUtc)
        {
            return true;
        }

        if (nowUtc - state.WindowStartedAtUtc >= FailureWindow)
        {
            authenticationFailures.Remove(userId);
        }

        return false;
    }

    private void RecordAuthenticationFailure(string userId, DateTimeOffset nowUtc)
    {
        if (!authenticationFailures.TryGetValue(userId, out AuthenticationFailureState? state)
            || nowUtc - state.WindowStartedAtUtc >= FailureWindow)
        {
            authenticationFailures[userId] = new AuthenticationFailureState(nowUtc, 1, null);
            return;
        }

        int failureCount = state.FailureCount + 1;
        authenticationFailures[userId] = state with
        {
            FailureCount = failureCount,
            LockedUntilUtc = failureCount >= MaximumFailures ? nowUtc.Add(LockoutDuration) : null
        };
    }

    private sealed record OfflineAccountPersistence(IReadOnlyList<OfflineAccountRecord> Accounts);
    private sealed record AuthenticationFailureState(
        DateTimeOffset WindowStartedAtUtc,
        int FailureCount,
        DateTimeOffset? LockedUntilUtc);

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
