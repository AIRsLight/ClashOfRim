using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal interface IClientCompatibilityModOrderSource
{
    string SteamPostfix { get; }
    IReadOnlyList<string>? ReadPackageOrder();
}

// Both the administrator editor and pre-login recovery bind the same comparison
// and one-shot override to their own lifetime/admission boundary.
internal sealed class ClientCompatibilityOverrideIntent
{
    private readonly IClientCompatibilityModOrderSource source;
    private readonly ClientCompatibilityOverride operation;
    private readonly Func<Action, bool> tryUse;
    private readonly Func<bool> available;
    private readonly string[]? localOrder;
    private bool used;
    internal bool RequiresWarning { get; }
    internal bool CanStart => !used && available();

    private ClientCompatibilityOverrideIntent(IClientCompatibilityModOrderSource source, ClientCompatibilityOverride operation,
        Func<Action, bool> tryUse, Func<bool> available, string[]? localOrder, bool warning)
    { this.source = source; this.operation = operation; this.tryUse = tryUse; this.available = available; this.localOrder = localOrder; RequiresWarning = warning; }

    internal static ClientCompatibilityOverrideIntent? Prepare(IClientCompatibilityModOrderSource source, ClientCompatibilityOverride operation,
        IReadOnlyList<string>? accepted, Func<Action, bool> tryUse, Func<bool> available)
    {
        ClientCompatibilityOverrideIntent? intent = null;
        tryUse(() =>
        {
            var local = Read(source);
            bool warning = local is null || accepted is null || !accepted.Select(id => Normalize(id, source.SteamPostfix)).SequenceEqual(local, StringComparer.Ordinal);
            tryUse(() => intent = new(source, operation, tryUse, available, local, warning));
        });
        return intent;
    }
    internal Task? StartAsync(Action<string, float>? progress, Action<string, bool> completion)
    {
        Task? work = null;
        tryUse(() =>
        {
            if (used) return;
            var current = Read(source);
            if (localOrder is not null && (current is null || !localOrder.SequenceEqual(current, StringComparer.Ordinal))) return;
            tryUse(() =>
            {
                if (used) return;
                used = true;
                work = operation.StartAsync(progress, completion);
            });
        });
        return work;
    }
    private static string[]? Read(IClientCompatibilityModOrderSource source)
        => source.ReadPackageOrder()?.Select(id => Normalize(id, source.SteamPostfix)).ToArray();
    private static string Normalize(string? id, string postfix)
    {
        string value = (id ?? "").Trim();
        if (postfix.Length > 0 && value.EndsWith(postfix, StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - postfix.Length);
        return value.ToLowerInvariant();
    }
}
