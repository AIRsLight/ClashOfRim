using System;
using System.Collections.Generic;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

// Owners are compared by identity: a new scope can have equal record contents
// while its predecessor's cached tab callbacks are no longer authorized.
internal sealed class MultiplayerTabCache<TKey>
{
    private object? owner, language;
    private TKey key = default!;
    private List<TabRecord>? tabs;
    internal List<TabRecord> Read(object currentOwner, TKey currentKey, object? currentLanguage, Func<List<TabRecord>> build)
    {
        if (tabs is not null && ReferenceEquals(owner, currentOwner) && ReferenceEquals(language, currentLanguage)
            && EqualityComparer<TKey>.Default.Equals(key, currentKey)) return tabs;
        var built = build();
        owner = currentOwner; language = currentLanguage; key = currentKey;
        return tabs = built;
    }
    internal void Clear() { owner = null; language = null; tabs = null; key = default!; }
}
