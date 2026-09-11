namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// Removes a test's temp directory, tolerating Windows taking a moment to let go of it.
///
/// Several of these tests start real child processes -- the Claude CLI on a pseudo-terminal,
/// esbuild's watcher -- whose working directory is inside the temp root. Killing a process
/// does not release the directory synchronously, so a plain recursive delete in teardown
/// fails intermittently with "being used by another process" and fails the test that just
/// passed. The production path has the same problem and solves it the same way
/// (ProjectTrash.TrashAsync); here it is only cleanup, so it gives up quietly rather than
/// turning a leaked temp directory into a red build.
/// </summary>
public static class TempRoot
{
    public static void Remove(string root)
    {
        if (!Directory.Exists(root)) return;

        var delay = TimeSpan.FromMilliseconds(50);

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 6) return;
                Thread.Sleep(delay);
                delay *= 2;   // 50, 100, 200, 400, 800ms -- ~1.5s before leaving it behind.
            }
        }
    }
}
