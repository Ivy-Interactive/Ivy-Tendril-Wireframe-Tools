using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Ivy.Tendril.Wireframe.Console.Project;

namespace Ivy.Tendril.Wireframe.Studio.Agent;

/// <summary>
/// Runs the Claude CLI against one wireframe project and relays its streaming output.
///
/// `claude -p --output-format stream-json` emits one JSON object per line, so the whole
/// turn can be forwarded to the browser as it happens rather than buffered. Multi-turn
/// continuity comes from a per-project session id passed to --resume.
///
/// Deliberately NOT --dangerously-skip-permissions. The agent gets an explicit allowlist:
/// the file tools it needs to edit a wireframe, plus `wireframe screenshot` so it can look
/// at its own work. Everything else still prompts, and a prompt in a non-interactive run
/// is a refusal -- which is the safe direction.
/// </summary>
public sealed class AgentSession(WireframeProject project, string wireframeCliPath)
{
    private static readonly ConcurrentDictionary<string, string> SessionIds = new(StringComparer.OrdinalIgnoreCase);

    private Process? _process;

    public sealed record Line(string Json);

    /// <summary>True when the `claude` CLI is on PATH.</summary>
    public static bool IsAvailable => FindClaude() is not null;

    private static string? FindClaude()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        var names = OperatingSystem.IsWindows()
            ? new[] { "claude.exe", "claude.cmd", "claude.bat" }
            : ["claude"];

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
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
        }
        return null;
    }

    /// <summary>
    /// The system prompt appended to the agent's own. Keeps it inside the conventions the
    /// component library expects without re-sending the whole 40 KB reference every turn --
    /// it is told to read AGENT.md, which `Run` writes into the project.
    /// </summary>
    private string BuildSystemPrompt() =>
        string.Join('\n',
        [
            "You are editing a Tendril wireframe mockup in a live Studio session.",
            "",
            $"Project: {project.Root}",
            "",
            "Read ./AGENT.md first if you have not already this session -- it is the complete",
            "reference for the component library and the CLI. Then edit files under src/.",
            "",
            "Rules:",
            "- Keep SketchProvider, the \"tendril\" class and signalWireframeReady() in src/main.tsx.",
            "- Only these imports resolve: tendril-wireframes, react, react-dom/client, lucide-react.",
            "  There is no package manager here. Do not add dependencies or write a package.json.",
            "- Props are named enums in PascalCase: variant=\"Destructive\", density=\"Small\".",
            "- Arbitrary Tailwind values (w-[347px]) are not generated. Use style={{ }} or a",
            "  component's own size prop.",
            "- Structural layout is plain flex/grid; anything that should look drawn is a component.",
            "",
            "The Studio shows the user a live preview that reloads as you save, so you do not need",
            "to run a server. To check your own work visually, run:",
            $"    \"{wireframeCliPath}\" screenshot \"{project.Root}\"",
            "and read the PNG it writes under screenshots/. Never run `wireframe serve` -- it blocks.",
            "",
            "Keep replies short: a sentence or two on what you changed.",
        ]);

    /// <summary>
    /// Starts a turn and yields the CLI's JSONL output line by line.
    /// </summary>
    public async IAsyncEnumerable<Line> RunAsync(
        string message,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var claude = FindClaude();
        if (claude is null)
        {
            yield return Error(
                "The `claude` CLI is not on PATH. Install it with `npm install -g @anthropic-ai/claude-code`, " +
                "then sign in by running `claude` once.");
            yield break;
        }

        // Give the agent the component reference, refreshed each turn so a tool upgrade
        // cannot leave a stale copy behind.
        await WriteAgentReadmeAsync(ct);

        var promptFile = Path.Combine(Path.GetTempPath(), $"wireframe-studio-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(promptFile, BuildSystemPrompt(), ct);

        var psi = new ProcessStartInfo(claude)
        {
            WorkingDirectory = project.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        foreach (var arg in BuildArguments(message, promptFile)) psi.ArgumentList.Add(arg);
        psi.Environment["FORCE_COLOR"] = "0";

        // An iterator cannot yield from a catch block, so the failure is captured first.
        Process? process = null;
        string? startError = null;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception e)
        {
            startError = e.Message;
        }

        if (process is null)
        {
            try { File.Delete(promptFile); } catch { /* best effort */ }
            yield return Error($"Could not start the Claude CLI: {startError ?? "unknown error"}");
            yield break;
        }

        _process = process;
        // Close stdin: the CLI must not wait on it in -p mode.
        try { process.StandardInput.Close(); } catch { /* already closed */ }

        var stderr = new System.Text.StringBuilder();
        _ = Task.Run(async () =>
        {
            try { stderr.Append(await process.StandardError.ReadToEndAsync(ct)); } catch { }
        }, CancellationToken.None);

        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Capture the session id from the init event so the next turn can resume.
                TryCaptureSessionId(line);
                yield return new Line(line);
            }

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 && stderr.Length > 0)
                yield return Error(Tail(stderr.ToString(), 12));
        }
        finally
        {
            Kill();
            try { File.Delete(promptFile); } catch { /* best effort */ }
        }
    }

    private List<string> BuildArguments(string message, string promptFile)
    {
        var args = new List<string>
        {
            "-p", message,
            "--output-format", "stream-json",
            "--verbose",
            "--append-system-prompt-file", promptFile,
            "--permission-mode", "acceptEdits",
            "--add-dir", project.Root,
            "--allowedTools",
            "Read", "Write", "Edit", "Glob", "Grep",
            // Only the screenshot verb, so the agent can see its own work without being
            // handed general shell access.
            "Bash(wireframe screenshot:*)",
            $"Bash({wireframeCliPath} screenshot:*)",
        };

        // Multi-turn: resume this project's conversation if we have one.
        if (SessionIds.TryGetValue(project.Root, out var sessionId))
        {
            args.Add("--resume");
            args.Add(sessionId);
        }

        return args;
    }

    private void TryCaptureSessionId(string line)
    {
        if (SessionIds.ContainsKey(project.Root)) return;
        if (!line.Contains("\"session_id\"", StringComparison.Ordinal)) return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("session_id", out var id) &&
                id.GetString() is { Length: > 0 } value)
            {
                SessionIds[project.Root] = value;
            }
        }
        catch (JsonException)
        {
            // Not our concern; the line is still forwarded.
        }
    }

    /// <summary>Writes the component reference into the project so the agent can read it.</summary>
    private async Task WriteAgentReadmeAsync(CancellationToken ct)
    {
        try
        {
            var assets = Console.Assets.AssetCatalog.Default;
            var renderer = new Console.Manifest.AgentReadmeRenderer(
                Console.Manifest.ComponentManifest.Load(assets),
                Console.Assets.VendorManifest.Load(assets));

            await File.WriteAllTextAsync(
                Path.Combine(project.Root, "AGENT.md"), renderer.Render(), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The agent can still work from its own knowledge; not worth failing the turn.
        }
    }

    /// <summary>Drops this project's conversation, so the next turn starts fresh.</summary>
    public void Reset() => SessionIds.TryRemove(project.Root, out _);

    public void Kill()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static Line Error(string message) =>
        new(JsonSerializer.Serialize(new { type = "error", message }));

    private static string Tail(string text, int lines)
    {
        var all = text.Replace("\r\n", "\n").TrimEnd().Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }
}
