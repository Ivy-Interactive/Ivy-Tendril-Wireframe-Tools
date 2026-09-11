using System.Text;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Ivy.Tendril.Wireframe.Studio.Terminal;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// Starts the real Claude Code CLI through PtySession, which is the exact path the Studio
/// terminal uses. PtyTests proves the pseudo-terminal works with a shell; this proves the
/// CLI in particular launches, since the two failed independently.
/// </summary>
public class ClaudePtyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wireframe-claude-pty", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        TempRoot.Remove(_root);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Claude_starts_and_writes_to_the_terminal()
    {
        var claude = PtySession.FindClaude();
        if (claude is null) Assert.Skip("The `claude` CLI is not on PATH.");

        var project = WireframeProject.At(_root);
        new ProjectScaffolder(AssetCatalog.Default).Scaffold(project);

        await using var session = await PtySession.StartAsync(
            project, claude!, "wireframe", 120, 30, TestContext.Current.CancellationToken);

        var text = await ReadForAsync(session, TimeSpan.FromSeconds(45));

        Assert.True(text.Length > 0,
            $"Claude produced no terminal output. Resolved CLI: {claude}");
    }

    private static async Task<string> ReadForAsync(PtySession session, TimeSpan timeout)
    {
        var text = new StringBuilder();
        var gate = new object();
        var enough = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var count = await session.Output.ReadAsync(buffer);
                    if (count <= 0) break;
                    lock (gate)
                    {
                        text.Append(Encoding.UTF8.GetString(buffer, 0, count));
                        // The TUI paints far more than the handshake once it is really up.
                        if (text.Length > 400) { enough.TrySetResult(true); return; }
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
            enough.TrySetResult(false);
        });

        await Task.WhenAny(enough.Task, Task.Delay(timeout));
        lock (gate)
        {
            var captured = text.ToString();
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "claude-pty-capture.txt"),
                "length=" + captured.Length + "\n---\n" + captured);
            return captured;
        }
    }
}
