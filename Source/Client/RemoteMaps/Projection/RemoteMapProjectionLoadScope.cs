using System;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteMapProjectionLoadScope
{
    [ThreadStatic]
    private static int depth;

    public static bool Active => depth > 0;

    public static IDisposable Begin()
    {
        depth++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (depth > 0)
            {
                depth--;
            }
        }
    }
}
