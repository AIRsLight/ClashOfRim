// A directory at a file's path rejects both replacement and deletion on Windows and Unix.
// Preserve the original bytes so removing the fault still exercises durable recovery.
internal sealed class FileMutationBlock : IDisposable
{
    private readonly string path;
    private readonly string preservedPath;
    private bool restored;

    internal FileMutationBlock(string path)
    {
        this.path = path;
        preservedPath = path + ".held-" + Guid.NewGuid().ToString("N");
        File.Move(path, preservedPath);
        try { Directory.CreateDirectory(path); }
        catch
        {
            File.Move(preservedPath, path);
            throw;
        }
    }

    public void Dispose()
    {
        if (restored) return;
        Directory.Delete(path);
        File.Move(preservedPath, path);
        restored = true;
    }
}
