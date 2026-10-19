using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

// Patch-free isolation: CLR generic sharing can let a DefDatabase<ThingDef>
// Harmony replacement affect unrelated DefDatabase<T> calls in later tests.
internal sealed class DefDatabaseTestScope<T> : IDisposable where T : Def, new()
{
    private readonly T[] previous = DefDatabase<T>.AllDefsListForReading.ToArray();

    internal void Replace(IEnumerable<T> definitions)
    {
        DefDatabase<T>.Clear();
        foreach (var definition in definitions) DefDatabase<T>.Add(definition);
        DefDatabase<T>.InitializeShortHashDictionary();
    }

    public void Dispose() => Replace(previous);
}
