using System.Text;
using Porta.Pty;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// Exercises the pseudo-terminal directly, without Claude or a WebSocket in the way.
///
/// Written because the terminal panel first failed with nothing but "session ended" in the
/// browser: with the CLI, the PTY and the socket all in one path there was no way to tell
/// which had broken. These are what proved a hand-rolled ConPTY was attaching the console
/// but never the child's stdout, and what confirms the replacement actually works.
/// </summary>
public class PtyTests
{
    private static string Shell => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static PtyOptions Options(string[] commandLine) => new()
    {
        Name = "xterm-256color",
        Cols = 80,
        Rows = 25,
        Cwd = Path.GetTempPath(),
        App = Shell,
        CommandLine = commandLine,
        VerbatimCommandLine = OperatingSystem.IsWindows(),
        Environment = new Dictionary<string, string> { ["TERM"] = "xterm-256color" },
    };

    [Fact]
    public async Task Runs_a_command_and_returns_its_output()
    {
        var pty = await PtyProvider.SpawnAsync(
            Options(OperatingSystem.IsWindows() ? ["/c echo pty-is-alive"] : ["-c", "echo pty-is-alive"]),
            TestContext.Current.CancellationToken);

        try
        {
            var text = await ReadForAsync(pty, TimeSpan.FromSeconds(20), "pty-is-alive");
            Assert.Contains("pty-is-alive", Describe(text));
        }
        finally
        {
            Kill(pty);
        }
    }

    [Fact]
    public async Task Keystrokes_reach_the_child()
    {
        // An interactive shell only echoes this back if it genuinely read our input, which
        // is the property the terminal panel depends on. A redirected pipe would not do it.
        var pty = await PtyProvider.SpawnAsync(
            Options(OperatingSystem.IsWindows() ? [""] : []),
            TestContext.Current.CancellationToken);

        try
        {
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            var keys = Encoding.UTF8.GetBytes("echo typed-ok\r");
            await pty.WriterStream.WriteAsync(keys, TestContext.Current.CancellationToken);
            await pty.WriterStream.FlushAsync(TestContext.Current.CancellationToken);

            var text = await ReadForAsync(pty, TimeSpan.FromSeconds(20), "typed-ok");
            Assert.Contains("typed-ok", Describe(text));
        }
        finally
        {
            Kill(pty);
        }
    }

    [Fact]
    public async Task Environment_overrides_reach_the_child()
    {
        var pty = await PtyProvider.SpawnAsync(
            Options(OperatingSystem.IsWindows()
                ? ["/c echo TERM=%TERM%"]
                : ["-c", "echo TERM=$TERM"]),
            TestContext.Current.CancellationToken);

        try
        {
            var text = await ReadForAsync(pty, TimeSpan.FromSeconds(20), "xterm-256color");
            Assert.Contains("xterm-256color", Describe(text));
        }
        finally
        {
            Kill(pty);
        }
    }

    [Fact]
    public async Task Resize_does_not_throw_while_the_child_is_running()
    {
        var pty = await PtyProvider.SpawnAsync(
            Options(OperatingSystem.IsWindows() ? [""] : []),
            TestContext.Current.CancellationToken);

        try
        {
            await Task.Delay(700, TestContext.Current.CancellationToken);
            pty.Resize(120, 40);
            pty.Resize(60, 20);
        }
        finally
        {
            Kill(pty);
        }
    }

    private static void Kill(IPtyConnection pty)
    {
        try { pty.Kill(); } catch { /* already exited */ }
        (pty as IDisposable)?.Dispose();
    }

    /// <summary>Terminal output is mostly invisible control codes, so a failure needs the
    /// escapes made printable or the assertion message is unreadable.</summary>
    private static string Describe(string text) =>
        text.Length == 0
            ? "<the pty produced no output>"
            : text.Replace("", "<ESC>").Replace("\r", "<CR>").Replace("\n", "<LF>");

    /// <summary>
    /// Reads until the marker appears, the stream ends, or the deadline passes.
    ///
    /// One outstanding read at a time. Racing a read against a timeout and moving on
    /// abandons the pending operation, and a second concurrent read on the same stream
    /// silently loses bytes -- which presents exactly like "the PTY produced no output".
    /// </summary>
    private static async Task<string> ReadForAsync(IPtyConnection pty, TimeSpan timeout, string marker)
    {
        var text = new StringBuilder();
        var gate = new object();
        var found = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var count = await pty.ReaderStream.ReadAsync(buffer);
                    if (count <= 0) break;

                    lock (gate)
                    {
                        text.Append(Encoding.UTF8.GetString(buffer, 0, count));
                        if (text.ToString().Contains(marker, StringComparison.Ordinal))
                        {
                            found.TrySetResult(true);
                            return;
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The child exited and the pty closed.
            }
            found.TrySetResult(false);
        });

        await Task.WhenAny(found.Task, Task.Delay(timeout));
        lock (gate) return text.ToString();
    }
}
