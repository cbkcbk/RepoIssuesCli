using System.Diagnostics;

namespace RepoCli.Core.Features.Repo;

public sealed class LockTimeoutException(string path)
    : Exception($"Timed out waiting for {path}. Another command is still writing to this folder; try again.");

public static class FolderLock
{
    public const string FileName = ".lock";
    const string IgnoreFileName = ".gitignore";
    static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    public static IDisposable Acquire(string folder, TimeSpan timeout)
    {
        Directory.CreateDirectory(folder);
        var lease = Open(Path.Combine(folder, FileName), timeout);
        if (!File.Exists(Path.Combine(folder, IgnoreFileName)))
            WriteIgnoreFile(folder);
        return lease;
    }

    public static void WriteIgnoreFile(string folder) => File.WriteAllText(Path.Combine(folder, IgnoreFileName), FileName + "\n");

    static FileStream Open(string path, TimeSpan timeout)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waited.Elapsed < timeout)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (IOException)
            {
                throw new LockTimeoutException(path);
            }
        }
    }
}
