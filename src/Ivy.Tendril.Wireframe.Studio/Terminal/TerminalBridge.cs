using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ivy.Tendril.Wireframe.Console.Project;

namespace Ivy.Tendril.Wireframe.Studio.Terminal;

/// <summary>
/// Plumbs one xterm.js instance to one PTY over a WebSocket.
///
/// Protocol, deliberately minimal:
///   browser -> server   raw bytes are keystrokes; a text frame starting with "\x00"
///                       is a JSON control message (currently only resize)
///   server -> browser   raw terminal output, binary frames
///
/// Keystrokes are sent as binary and control messages as text, so the two never need
/// disambiguating by content -- a user typing a literal JSON blob into the terminal must
/// not be mistaken for a resize.
/// </summary>
public sealed class TerminalBridge(WireframeProject project, string wireframeCli)
{
    private const int BufferSize = 16 * 1024;

    // Named rather than inline. A literal ESC byte is invisible in most editors and
    // diffs, so it is easy to drop in an edit and end up printing "[33m" as text.
    private const string Yellow = "[33m";
    private const string Red = "[31m";
    private const string Reset = "[0m";

    public async Task RunAsync(WebSocket socket, CancellationToken ct)
    {
        var claude = PtySession.FindClaude();
        if (claude is null)
        {
            await SayAsync(socket,
                $"\r\n{Yellow}The `claude` CLI is not on PATH.{Reset}\r\n" +
                "Install it with:  npm install -g @anthropic-ai/claude-code\r\n" +
                "Then sign in by running `claude` once in a terminal.\r\n", ct);
            return;
        }

        if (!PtySession.IsSupported)
        {
            await SayAsync(socket, $"\r\n{Yellow}{PtySession.UnsupportedReason}{Reset}\r\n", ct);
            return;
        }

        PtySession session;
        try
        {
            session = await PtySession.StartAsync(project, claude, wireframeCli, 120, 30, ct);
        }
        catch (Exception e)
        {
            await SayAsync(socket, $"\r\n{Red}Could not start Claude Code:{Reset} {e.Message}\r\n", ct);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // A process that dies at once is a launch failure, not a finished session --
            // report it rather than letting it look like the user quit.
            await Task.Delay(250, ct);
            if (session.HasExited)
            {
                await SayAsync(socket,
                    $"\r\n{Red}Claude Code exited immediately.{Reset}\r\n" +
                    $"Tried: {claude}\r\nIn: {project.Root}\r\n", ct);
                return;
            }

            // Both directions run until either the socket closes or the CLI exits; the
            // linked token is what makes one ending stop the other.
            await Task.WhenAny(
                PumpOutputAsync(session, socket, linked),
                PumpInputAsync(session, socket, linked));
        }
        finally
        {
            await linked.CancelAsync();
            await session.DisposeAsync();
        }
    }

    /// <summary>PTY output -> browser.</summary>
    private static async Task PumpOutputAsync(
        PtySession session, WebSocket socket, CancellationTokenSource cts)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                // Not passing the token: a cancelled synchronous pipe read on Windows can
                // leave the handle in a bad state. Closing the PTY is what ends this loop.
                var read = await session.Output.ReadAsync(buffer, CancellationToken.None);
                if (read <= 0) break;

                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, read),
                    WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or WebSocketException)
        {
            // The CLI exited or the browser went away.
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    /// <summary>Browser -> PTY, plus control messages.</summary>
    private static async Task PumpInputAsync(
        PtySession session, WebSocket socket, CancellationTokenSource cts)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (!cts.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleControl(session, Encoding.UTF8.GetString(buffer, 0, result.Count));
                    continue;
                }

                await session.Input.WriteAsync(
                    buffer.AsMemory(0, result.Count), CancellationToken.None);
                await session.Input.FlushAsync(CancellationToken.None);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException
                                       or ObjectDisposedException or WebSocketException)
        {
            // Normal teardown.
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    private static void HandleControl(PtySession session, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "resize")
            {
                session.Resize(
                    root.GetProperty("cols").GetInt32(),
                    root.GetProperty("rows").GetInt32());
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A malformed control frame is not worth dropping the session over.
        }
    }

    /// <summary>Writes a message into the terminal itself, so failures are visible where
    /// the user is already looking rather than in a console they cannot see.</summary>
    private static async Task SayAsync(WebSocket socket, string text, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open) return;
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, true, ct);
    }
}
