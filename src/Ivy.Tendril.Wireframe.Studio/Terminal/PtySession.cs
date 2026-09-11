using Ivy.Tendril.Wireframe.Console.Project;
using Porta.Pty;

namespace Ivy.Tendril.Wireframe.Studio.Terminal;

/// <summary>
/// A Claude Code session running on a real terminal, with its output streamed to xterm.js.
///
/// This has to be a pseudo-terminal rather than redirected pipes. Claude Code checks
/// whether stdout is a TTY; on a pipe it drops to non-interactive mode, so there would be
/// no prompt to type into and no TUI to render -- which is the whole point of showing a
/// terminal instead of a chat box.
///
/// Porta.Pty does the platform work: Microsoft's official ConPTY binding on Windows, and
/// native shims on Linux and macOS. This started as a hand-rolled ConPTY P/Invoke, which
/// got as far as the console handshake but never attached the child's stdout to it -- the
/// Ivy framework already depends on this package for the same job, so there was no reason
/// to keep maintaining the interop.
/// </summary>
public sealed class PtySession : IAsyncDisposable
{
    private readonly IPtyConnection _connection;

    private PtySession(IPtyConnection connection)
    {
        _connection = connection;
        connection.ProcessExited += (_, _) => HasExited = true;
    }

    public Stream Input => _connection.WriterStream;
    public Stream Output => _connection.ReaderStream;

    public bool HasExited { get; private set; }

    /// <summary>Whether an interactive session can be started at all here.</summary>
    public static bool IsSupported => true;

    public static string? UnsupportedReason => null;

    public static async Task<PtySession> StartAsync(
        WireframeProject project,
        string claudePath,
        string wireframeCli,
        int columns,
        int rows,
        CancellationToken ct = default)
    {
        // On Windows the command line must be passed verbatim -- the CLI is a .cmd shim and
        // Porta's default argument joining would mangle a path containing spaces.
        var verbatim = OperatingSystem.IsWindows();
        string[] arguments = ["--add-dir", project.Root];

        var options = new PtyOptions
        {
            Name = "xterm-256color",
            Cols = Math.Max(20, columns),
            Rows = Math.Max(5, rows),
            Cwd = project.Root,
            App = claudePath,
            CommandLine = verbatim
                ? [string.Join(" ", arguments.Select(Quote))]
                : arguments,
            VerbatimCommandLine = verbatim,
            Environment = BuildEnvironment(wireframeCli),
        };

        return new PtySession(await PtyProvider.SpawnAsync(options, ct));
    }

    /// <summary>Quotes an argument for a verbatim Windows command line.</summary>
    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Contains(' ') && !argument.Contains('"')
            ? argument
            : $"\"{argument.Replace("\"", "\\\"")}\"";

    private static Dictionary<string, string> BuildEnvironment(string wireframeCli)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        }

        // xterm.js speaks xterm-256color; without TERM the CLI assumes a dumb terminal and
        // renders no colour or cursor movement.
        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        environment["FORCE_COLOR"] = "1";

        // Put the `wireframe` CLI on PATH so the agent can screenshot its own work with a
        // bare command rather than an absolute path.
        var directory = Path.GetDirectoryName(wireframeCli);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            environment["PATH"] = $"{directory}{Path.PathSeparator}{path}";
        }

        return environment;
    }

    public void Resize(int columns, int rows)
    {
        try
        {
            _connection.Resize(Math.Max(20, columns), Math.Max(5, rows));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The session ended between the browser measuring and the resize arriving.
        }
    }

    /// <summary>Locates the Claude Code CLI, or null when it is not installed.</summary>
    public static string? FindClaude() =>
        OperatingSystem.IsWindows()
            ? FindOnPath("claude.exe") ?? FindOnPath("claude.cmd") ?? FindOnPath("claude.bat")
            : FindOnPath("claude");

    private static string? FindOnPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (!HasExited) _connection.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // Already gone.
        }

        (_connection as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
