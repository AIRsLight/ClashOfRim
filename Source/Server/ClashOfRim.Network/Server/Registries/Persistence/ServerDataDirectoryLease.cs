using System.Text;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ServerDataDirectoryLease : IDisposable
{
    private readonly FileStream stream;
    private bool disposed;

    private ServerDataDirectoryLease(FileStream stream)
    {
        this.stream = stream;
    }

    public static ServerDataDirectoryLease Acquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        string path = Path.Combine(dataDirectory, ".server.lock");
        FileStream stream = new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        try
        {
            stream.SetLength(0);
            string owner = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " "
                + DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            byte[] bytes = Encoding.UTF8.GetBytes(owner);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
            return new ServerDataDirectoryLease(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stream.Dispose();
    }
}
