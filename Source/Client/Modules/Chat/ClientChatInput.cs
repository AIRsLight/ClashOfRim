using System;

namespace AIRsLight.ClashOfRim.Modules.Chat;

internal sealed record ClientChatInput(string? Target, string Text, string? Failure = null)
{
    internal static ClientChatInput Parse(string? raw)
    {
        string text = (raw ?? "").Trim(); string? target = null;
        if (text.Length == 0) return new(null, "", "ClashOfRim.Chat.EmptyMessage");
        if (text.StartsWith("@", StringComparison.Ordinal))
        {
            int separator = text.IndexOf(' ');
            if (separator <= 1) return new(null, "", "ClashOfRim.Chat.InvalidPrivateFormat");
            target = text.Substring(1, separator - 1).Trim(); text = text.Substring(separator + 1).Trim();
            if (target.Length == 0 || text.Length == 0) return new(null, "", "ClashOfRim.Chat.InvalidPrivateFormat");
        }
        const int maximum = 500;
        if (text.Length > maximum)
        {
            // The wire limit is UTF-16 length; preserve a supplementary character
            // at the boundary instead of sending an unpaired surrogate.
            int end = char.IsHighSurrogate(text[maximum - 1]) && char.IsLowSurrogate(text[maximum]) ? maximum - 1 : maximum;
            text = text.Substring(0, end);
        }
        return new(target, text);
    }
}
