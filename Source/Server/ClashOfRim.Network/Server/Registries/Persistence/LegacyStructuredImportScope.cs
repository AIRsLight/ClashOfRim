using System.Threading;

namespace AIRsLight.ClashOfRim.Network;

/// <summary>
/// Makes legacy JSON imports an explicit migration-only operation. Registry
/// construction must not populate structured stores during normal startup.
/// </summary>
internal static class LegacyStructuredImportScope
{
    private static readonly AsyncLocal<int> Depth = new();

    public static bool IsActive => Depth.Value > 0;

    public static IDisposable Begin()
    {
        Depth.Value++;
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
            Depth.Value = Math.Max(0, Depth.Value - 1);
        }
    }
}
