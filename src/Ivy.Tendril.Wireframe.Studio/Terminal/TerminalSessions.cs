using System.Collections.Concurrent;
using System.Net.WebSockets;
using Ivy.Tendril.Wireframe.Console.Project;

namespace Ivy.Tendril.Wireframe.Studio.Terminal;

/// <summary>
/// Keeps one long-lived Claude Code session per wireframe, outliving the browser.
///
/// The session must not be tied to the WebSocket. Switching wireframes in the rail, or
/// just reloading the page, closes the socket -- and if that killed the PTY you would lose
/// the agent's context every time you looked at another mockup, which makes going back and
/// forth useless.
///
/// So the PTY is owned here and read continuously whether or not anyone is listening.
/// Output goes into a scrollback buffer; attaching a socket replays that buffer and then
/// streams live.
/// </summary>
public sealed class TerminalSessions(string wireframeCli) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Attaches a socket to this project's session, starting one if needed.</summary>
    public async Task ServeAsync(WireframeProject project, WebSocket socket, CancellationToken ct)
    {
        var session = _sessions.GetOrAdd(project.Root, _ => new TerminalSession(project, wireframeCli));

        // A session whose CLI has exited is dead weight; replace it rather than attaching
        // a socket to a corpse.
        if (session.HasExited)
        {
            await RemoveAsync(project.Root);
            session = _sessions.GetOrAdd(project.Root, _ => new TerminalSession(project, wireframeCli));
        }

        await session.AttachAsync(socket, ct);
    }

    /// <summary>Ends a project's session, e.g. on an explicit restart or a delete.</summary>
    public async Task RemoveAsync(string projectRoot)
    {
        if (_sessions.TryRemove(projectRoot, out var session)) await session.DisposeAsync();
    }

    public bool IsRunning(string projectRoot) =>
        _sessions.TryGetValue(projectRoot, out var session) && !session.HasExited;

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _sessions.Keys.ToList()) await RemoveAsync(key);
    }
}

/// <summary>One project's terminal: the PTY, its scrollback, and the socket currently
/// watching it.</summary>
public sealed class TerminalSession(WireframeProject project, string wireframeCli) : IAsyncDisposable
{
    /// <summary>
    /// How much output to keep for replay. Claude Code's TUI repaints constantly, so this
    /// is far more than one screen -- but bounded, because a session left running for
    /// hours would otherwise grow without limit.
    /// </summary>
    private const int ScrollbackBytes = 512 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MemoryStream _scrollback = new();

    private PtySession? _pty;
    private WebSocket? _socket;
    private Task? _pump;
    private CancellationTokenSource? _life;

    public bool HasExited => _pty?.HasExited ?? false;

    /// <summary>Whether the CLI failed to start; the message is already in the scrollback.</summary>
    public bool Failed { get; private set; }

    public async Task AttachAsync(WebSocket socket, CancellationToken ct)
    {
        await EnsureStartedAsync(ct);

        await _gate.WaitAsync(ct);
        try
        {
            _socket = socket;
            await ReplayAsync(socket, ct);
        }
        finally
        {
            _gate.Release();
        }

        if (Failed) return;

        // Nudge the CLI into a full repaint. After a replay the screen is a transcript of
        // everything that ever happened; a resize makes the TUI redraw its current state
        // over the top, which is what the user expects to see.
        Resize(120, 30);

        await PumpInputAsync(socket, ct);

        // The socket is going away but the session stays alive.
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (ReferenceEquals(_socket, socket)) _socket = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_pty is not null) return;

            var claude = PtySession.FindClaude();
            if (claude is null)
            {
                Failed = true;
                Append(System.Text.Encoding.UTF8.GetBytes(
                    "\r\n[33mThe `claude` CLI is not on PATH.[0m\r\n" +
                    "Install it with:  npm install -g @anthropic-ai/claude-code\r\n" +
                    "Then sign in by running `claude` once in a terminal.\r\n"));
                return;
            }

            try
            {
                _pty = await PtySession.StartAsync(project, claude, wireframeCli, 120, 30, ct);
            }
            catch (Exception e)
            {
                Failed = true;
                Append(System.Text.Encoding.UTF8.GetBytes(
                    $"\r\n[31mCould not start Claude Code:[0m {e.Message}\r\n"));
                return;
            }

            _life = new CancellationTokenSource();
            _pump = Task.Run(() => PumpOutputAsync(_life.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the PTY for the life of the session, not the life of a socket. This is the
    /// whole point: with nobody attached the output still has to be drained, or the CLI
    /// blocks on a full pipe and the session wedges.
    /// </summary>
    private async Task PumpOutputAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await _pty!.Output.ReadAsync(buffer, CancellationToken.None);
                if (read <= 0) break;

                var chunk = buffer.AsMemory(0, read);

                await _gate.WaitAsync(CancellationToken.None);
                WebSocket? socket;
                try
                {
                    Append(chunk.Span);
                    socket = _socket;
                }
                finally
                {
                    _gate.Release();
                }

                if (socket is { State: WebSocketState.Open })
                {
                    try
                    {
                        await socket.SendAsync(chunk, WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                    catch (Exception e) when (e is WebSocketException or ObjectDisposedException
                                                   or InvalidOperationException)
                    {
                        // Browser vanished mid-send; the session carries on regardless.
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The CLI exited.
        }
    }

    private async Task PumpInputAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Answer the client's close frame. Without this the handshake never
                    // finishes and the browser sees every wireframe switch as an abnormal
                    // close, which is indistinguishable from the server having crashed.
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        await socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleControl(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));
                    continue;
                }

                if (_pty is null) continue;
                await _pty.Input.WriteAsync(buffer.AsMemory(0, result.Count), CancellationToken.None);
                await _pty.Input.FlushAsync(CancellationToken.None);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException
                                       or ObjectDisposedException or WebSocketException)
        {
            // Normal detach.
        }
    }

    private void HandleControl(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "resize")
            {
                Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            // A malformed control frame is not worth dropping the session over.
        }
    }

    private void Resize(int columns, int rows) => _pty?.Resize(columns, rows);

    /// <summary>Appends to the scrollback, trimming from the front once it is full.</summary>
    private void Append(ReadOnlySpan<byte> chunk)
    {
        _scrollback.Write(chunk);
        if (_scrollback.Length <= ScrollbackBytes) return;

        // Keep the tail: the newest output is what reconstructs the current screen.
        var kept = _scrollback.GetBuffer()
            .AsSpan((int)_scrollback.Length - ScrollbackBytes, ScrollbackBytes)
            .ToArray();

        _scrollback.SetLength(0);
        _scrollback.Write(kept);
    }

    private async Task ReplayAsync(WebSocket socket, CancellationToken ct)
    {
        if (_scrollback.Length == 0 || socket.State != WebSocketState.Open) return;

        var bytes = _scrollback.ToArray();
        await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_life is not null) await _life.CancelAsync();

        if (_pty is not null)
        {
            await _pty.DisposeAsync();
            _pty = null;
        }

        if (_pump is not null)
        {
            try { await _pump; } catch { /* already reported */ }
        }

        _life?.Dispose();
        _scrollback.Dispose();
        _gate.Dispose();
    }
}
