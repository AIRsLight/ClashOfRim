using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using ReferenceEqualityComparer = AIRsLight.ClashOfRim.ThirdPartyCompatibility.ReferenceEqualityComparer;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal sealed class CompatibilityRegistrationScope
{
    private readonly Action<string> warning;
    private readonly Dictionary<IList, Action?> collections = new(new ReferenceEqualityComparer());
    private readonly List<(object Registration, CompatibilityRegistrationRecord Record)> RegistrationRecords = new();
    private readonly List<CompatibilityRegistrationDiagnostic> RegistrationDiagnostics = new();
    private readonly Dictionary<object, int> RegistrationPriorities = new(new ReferenceEqualityComparer());
    private readonly Dictionary<object, int> RegistrationOrders = new(new ReferenceEqualityComparer());
    private readonly Dictionary<int, CompatibilityRegistrationToken> RegistrationTokens = new();
    private readonly Dictionary<int, List<Action>> RegistrationCleanupsByToken = new();
    private int registrationGeneration;
    private int registrationTokenSequence;
    private int registrationOrderSequence;
    private CompatibilityRegistrationToken? activeRegistrationToken;

    internal CompatibilityRegistrationScope(Action<string> warning) => this.warning = warning;
    internal int CurrentRegistrationGeneration => registrationGeneration;
    internal IReadOnlyList<CompatibilityRegistrationRecord> Records => RegistrationRecords.Select(entry => entry.Record).ToArray();
    internal IReadOnlyList<CompatibilityRegistrationDiagnostic> Diagnostics => RegistrationDiagnostics.ToArray();

    internal CompatibilityRegistrationToken BeginRegistrationCycle(string owner, int priority = 0)
    {
        foreach (IList collection in collections.Keys) collection.Clear();
        RegistrationRecords.Clear();
        RegistrationDiagnostics.Clear();
        RegistrationPriorities.Clear();
        RegistrationOrders.Clear();
        registrationOrderSequence = 0;
        foreach (Action? changed in collections.Values) changed?.Invoke();
        collections.Clear();
        RegistrationTokens.Clear();
        RegistrationCleanupsByToken.Clear();
        registrationGeneration = unchecked(registrationGeneration + 1);
        activeRegistrationToken = CreateRegistrationTokenCore(owner, priority);
        return activeRegistrationToken;
    }

    internal void RegisterUnique<T>(List<T> registrations, T registration, Action? changed = null) where T : class
    {
        if (registration is null || registrations.Contains(registration)) return;
        Register(registrations, registration, CompatibilityRegistrationCategory(typeof(T)), RegistrationKey(registration),
            ActiveRegistrationPriority(), changed);
    }

    internal void Register<T>(List<T> registrations, T registration, string category, string key, int priority,
        Action? changed = null) where T : class
    {
        collections[registrations] = changed;
        registrations.Add(registration);
        RegistrationPriorities[registration] = priority;
        RegistrationOrders[registration] = unchecked(++registrationOrderSequence);
        SortRegistrationsByPriority(registrations);
        changed?.Invoke();
        CompatibilityRegistrationRecord record = AddRegistrationRecord(registration, category, key, priority);
        TrackRegistration(() =>
        {
            registrations.Remove(registration);
            RegistrationPriorities.Remove(registration);
            RegistrationOrders.Remove(registration);
            RegistrationRecords.RemoveAll(entry => ReferenceEquals(entry.Record, record));
            changed?.Invoke();
        });
    }

    internal void RegisterMetadataKeyedRegistration<T>(
        List<T> registrations,
        T registration,
        string category,
        string key,
        string duplicateRejectedCode,
        string duplicateReplacedCode, Action? changed = null)
        where T : class, IMetadataKeyedRegistration
    {
        if (registration is null || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        int priority = ActiveRegistrationPriority();
        string normalizedKey = key.Trim();
        T? existing = registrations.FirstOrDefault(item =>
            string.Equals(item.MetadataKey, normalizedKey, StringComparison.Ordinal));
        if (!TryReplaceDuplicate(registrations, existing, category, normalizedKey, priority,
                duplicateRejectedCode, duplicateReplacedCode, category + " '" + normalizedKey + "'", changed)) return;

        Register(registrations, registration, category, normalizedKey, priority, changed);
    }

    internal bool TryReplaceDuplicate<T>(List<T> registrations, T? existing, string category, string key, int priority,
        string duplicateRejectedCode, string duplicateReplacedCode, string description, Action? changed = null) where T : class
    {
        if (existing is null) return true;
        int existingPriority = RegistrationPriority(existing);
        bool rejected = priority <= existingPriority;
        AddRegistrationDiagnostic("Warning", rejected ? duplicateRejectedCode : duplicateReplacedCode, category, key,
            description + (rejected ? " was rejected because an existing registration has priority " : " replaced an existing registration with priority ")
                + existingPriority + ".");
        if (rejected) return false;
        registrations.Remove(existing);
        RemoveRegistrationTracking(existing);
        changed?.Invoke();
        return true;
    }

    internal CompatibilityRegistrationToken CreateRegistrationToken(string owner)
    {
        return CreateRegistrationTokenCore(owner, priority: 0);
    }

    internal CompatibilityRegistrationToken CreateRegistrationToken(string owner, int priority)
    {
        return CreateRegistrationTokenCore(owner, priority);
    }

    internal bool UseRegistrationToken(CompatibilityRegistrationToken token, Action registration)
    {
        if (!IsCurrentRegistrationToken(token) || registration is null)
        {
            return false;
        }

        CompatibilityRegistrationToken? previous = activeRegistrationToken;
        activeRegistrationToken = token;
        try
        {
            registration();
            return true;
        }
        finally
        {
            activeRegistrationToken = previous;
        }
    }

    internal bool RevokeRegistrationToken(CompatibilityRegistrationToken? token)
    {
        if (!IsCurrentRegistrationToken(token))
        {
            return false;
        }

        CompatibilityRegistrationToken currentToken = token!;
        bool hadRegistrations = false;
        if (RegistrationCleanupsByToken.TryGetValue(currentToken.Id, out List<Action>? cleanups))
        {
            hadRegistrations = cleanups.Count > 0;
            for (int i = cleanups.Count - 1; i >= 0; i--)
            {
                try
                {
                    cleanups[i]();
                }
                catch (Exception ex)
                {
                    warning("[ClashOfRim] Failed to revoke compatibility registration for " + currentToken.Owner + ": " + ex.Message);
                }
            }
        }

        RegistrationCleanupsByToken.Remove(currentToken.Id);
        RegistrationTokens.Remove(currentToken.Id);
        if (activeRegistrationToken?.Id == currentToken.Id)
        {
            activeRegistrationToken = null;
        }

        return hadRegistrations;
    }

    internal int RevokeRegistrationsByOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return 0;
        }

        int revoked = 0;
        string normalizedOwner = owner.Trim();
        foreach (CompatibilityRegistrationToken token in RegistrationTokens.Values.ToList())
        {
            if (string.Equals(token.Owner, normalizedOwner, StringComparison.OrdinalIgnoreCase)
                && RevokeRegistrationToken(token))
            {
                revoked++;
            }
        }

        return revoked;
    }

    private CompatibilityRegistrationToken CreateRegistrationTokenCore(string owner, int priority)
    {
        string normalizedOwner = string.IsNullOrWhiteSpace(owner) ? "unknown" : owner.Trim();
        var token = new CompatibilityRegistrationToken(
            normalizedOwner,
            registrationGeneration,
            unchecked(++registrationTokenSequence),
            priority);
        RegistrationTokens[token.Id] = token;
        RegistrationCleanupsByToken[token.Id] = new List<Action>();
        return token;
    }

    private bool IsCurrentRegistrationToken(CompatibilityRegistrationToken? token)
    {
        return token is not null
            && token.Generation == registrationGeneration
            && RegistrationTokens.TryGetValue(token.Id, out CompatibilityRegistrationToken? registered)
            && ReferenceEquals(registered, token);
    }

    private void TrackRegistration(Action cleanup)
    {
        CompatibilityRegistrationToken? token = activeRegistrationToken;
        if (!IsCurrentRegistrationToken(token))
        {
            return;
        }

        CompatibilityRegistrationToken currentToken = token!;
        if (!RegistrationCleanupsByToken.TryGetValue(currentToken.Id, out List<Action>? cleanups))
        {
            cleanups = new List<Action>();
            RegistrationCleanupsByToken[currentToken.Id] = cleanups;
        }

        cleanups.Add(cleanup);
    }

    internal int ActiveRegistrationPriority()
    {
        CompatibilityRegistrationToken? token = activeRegistrationToken;
        return IsCurrentRegistrationToken(token) ? token!.Priority : 0;
    }

    private void SortRegistrationsByPriority<T>(List<T> registrations)
        where T : class
    {
        registrations.Sort((left, right) =>
        {
            int priorityComparison = RegistrationPriority(right).CompareTo(RegistrationPriority(left));
            return priorityComparison != 0
                ? priorityComparison
                : RegistrationOrder(left).CompareTo(RegistrationOrder(right));
        });
    }

    private int RegistrationPriority(object registration)
    {
        return RegistrationPriorities.TryGetValue(registration, out int priority) ? priority : 0;
    }

    private int RegistrationOrder(object registration)
    {
        return RegistrationOrders.TryGetValue(registration, out int order) ? order : 0;
    }

    private void RemoveRegistrationTracking(object registration)
    {
        RegistrationPriorities.Remove(registration);
        RegistrationOrders.Remove(registration);
        RegistrationRecords.RemoveAll(entry => ReferenceEquals(entry.Registration, registration));
    }

    private CompatibilityRegistrationRecord AddRegistrationRecord(object registration, string category, string key, int priority)
    {
        CompatibilityRegistrationToken? token = activeRegistrationToken;
        var record = new CompatibilityRegistrationRecord(
            category,
            string.IsNullOrWhiteSpace(key) ? "<unknown>" : key,
            IsCurrentRegistrationToken(token) ? token!.Owner : "<untracked>",
            IsCurrentRegistrationToken(token) ? token!.Generation : registrationGeneration,
            priority);
        RegistrationRecords.Add((registration, record));
        return record;
    }

    internal void AddRegistrationDiagnostic(
        string severity,
        string code,
        string category,
        string key,
        string message)
    {
        var diagnostic = new CompatibilityRegistrationDiagnostic(
            severity,
            code,
            category,
            key,
            message);
        RegistrationDiagnostics.Add(diagnostic);
        if (string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase))
        {
            warning("[ClashOfRim] Compatibility registration " + code + ": " + message);
        }
    }

    internal bool TryInvokeCompatibilityCallback(
        string operation,
        object registration,
        Action callback)
    {
        try
        {
            callback();
            return true;
        }
        catch (Exception ex)
        {
            LogCompatibilityCallbackException(operation, registration, ex);
            return false;
        }
    }

    internal bool TryInvokeCompatibilityCallback<T>(
        string operation,
        object registration,
        Func<T> callback,
        out T result)
    {
        try
        {
            result = callback();
            return true;
        }
        catch (Exception ex)
        {
            LogCompatibilityCallbackException(operation, registration, ex);
            result = default!;
            return false;
        }
    }

    private void LogCompatibilityCallbackException(string operation, object registration, Exception ex)
    {
        string category = registration is null
            ? "<unknown>"
            : CompatibilityRegistrationCategory(registration.GetType());
        string key = registration is null ? "<unknown>" : RegistrationKey(registration);
        string message = "Compatibility callback failed during " + operation + ": "
            + ex.GetType().Name + " " + ex.Message;
        AddRegistrationDiagnostic(
            "Error",
            "CompatibilityCallbackException",
            category,
            key,
            message);
    }

    private string CompatibilityRegistrationCategory(Type type)
    {
        return type.Name;
    }

    internal static string RegistrationKey(object registration)
    {
        if (registration is Delegate callback)
        {
            return callback.Method.DeclaringType?.FullName + "." + callback.Method.Name;
        }

        return registration.GetType().FullName ?? registration.GetType().Name;
    }

}
