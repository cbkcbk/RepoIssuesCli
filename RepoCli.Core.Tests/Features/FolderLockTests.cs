namespace RepoCli.Core.Tests.Features;

public class FolderLockTests
{
    [Test]
    public void LockIsExclusiveUntilReleased()
    {
        using var folder = new TempFolder();
        var data = Path.Combine(folder.Root, ".repo-docs");

        using (FolderLock.Acquire(data, TimeSpan.FromSeconds(1)))
            Assert.Throws<LockTimeoutException>(() => FolderLock.Acquire(data, TimeSpan.FromMilliseconds(100)));

        Assert.DoesNotThrow(() => FolderLock.Acquire(data, TimeSpan.FromMilliseconds(100)).Dispose());
    }

    [Test]
    public void AcquireCreatesTheFolderAndIgnoresTheLockFile()
    {
        using var folder = new TempFolder();
        var data = Path.Combine(folder.Root, ".repo-docs");

        using (FolderLock.Acquire(data, TimeSpan.FromSeconds(1)))
            Assert.That(File.ReadAllText(Path.Combine(data, ".gitignore")), Is.EqualTo(".lock\n"));
    }
}
