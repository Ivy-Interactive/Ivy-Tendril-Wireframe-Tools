using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Ivy.Tendril.Wireframe.Studio.Hosting;
using Ivy.Tendril.Wireframe.Studio.Projects;
using Ivy.Tendril.Wireframe.Studio.Terminal;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// Drives the terminal end to end over the real WebSocket, which is the last untested
/// link: the PTY works with a shell, and with Claude, yet the browser still showed nothing
/// but "session ended".
/// </summary>
public class TerminalSocketTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wireframe-ws-tests", Guid.NewGuid().ToString("N")[..8]);

    private StudioServer _server = null!;

    public async ValueTask InitializeAsync()
    {
        var project = WireframeProject.At(Path.Combine(_root, "sample"));
        new ProjectScaffolder(AssetCatalog.Default).Scaffold(project);

        _server = new StudioServer(AssetCatalog.Default, new ProjectIndex(_root));
        await _server.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Terminal_socket_streams_output()
    {
        if (PtySession.FindClaude() is null) Assert.Skip("The `claude` CLI is not on PATH.");

        using var socket = new ClientWebSocket();
        var uri = new Uri(_server.Url.Replace("http://", "ws://") + "/api/projects/sample/pty");
        await socket.ConnectAsync(uri, TestContext.Current.CancellationToken);

        var text = new StringBuilder();
        var buffer = new byte[8192];
        var deadline = DateTime.UtcNow.AddSeconds(45);

        while (DateTime.UtcNow < deadline && text.Length < 400)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close) break;
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }

        Assert.True(text.Length > 0,
            "The terminal socket closed without sending anything. " +
            $"Received: {text.Length} bytes. Socket state: {socket.State}.");
    }

    /// <summary>
    /// Reads from a socket until it has seen <paramref name="minimum"/> bytes or the wait
    /// runs out. Returns what arrived rather than throwing, so an assertion can report the
    /// actual byte count -- "got nothing" and "got half a screen" are different bugs.
    /// </summary>
    private static async Task<string> DrainAsync(ClientWebSocket socket, int minimum, TimeSpan patience)
    {
        var text = new StringBuilder();
        var buffer = new byte[8192];
        var deadline = DateTime.UtcNow + patience;

        while (DateTime.UtcNow < deadline && text.Length < minimum)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close) break;
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }

        return text.ToString();
    }

    private Uri PtyUri(string project) =>
        new(_server.Url.Replace("http://", "ws://") + $"/api/projects/{project}/pty");

    /// <summary>
    /// Switching wireframes closes the socket. The agent's conversation must not go with
    /// it, or going back and forth between mockups loses the context every time -- so a
    /// second attach has to find the same session and replay what it missed.
    /// </summary>
    [Fact]
    public async Task Session_survives_a_detach_and_replays_on_reattach()
    {
        if (PtySession.FindClaude() is null) Assert.Skip("The `claude` CLI is not on PATH.");

        using var http = new HttpClient { BaseAddress = new Uri(_server.Url) };

        string first;
        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(PtyUri("sample"), TestContext.Current.CancellationToken);
            first = await DrainAsync(socket, 400, TimeSpan.FromSeconds(45));
            Assert.NotEqual(string.Empty, first);

            // What the browser does on a switch: drop the socket, leave the CLI alone.
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null,
                TestContext.Current.CancellationToken);
        }

        // The load-bearing assertion. Byte counts would not do: a fresh CLI paints its
        // banner in well under a second, so "output arrived quickly" cannot tell a replayed
        // session apart from a restarted one. Only the server knows which this is.
        Assert.True(await IsRunningAsync(http),
            "The session ended when its socket closed, so switching wireframes loses the "
            + "agent's context.");

        using var second = new ClientWebSocket();
        await second.ConnectAsync(PtyUri("sample"), TestContext.Current.CancellationToken);

        var replayed = await DrainAsync(second, first.Length, TimeSpan.FromSeconds(5));

        Assert.True(replayed.Length >= first.Length,
            $"Reattaching replayed {replayed.Length} bytes but the session had already "
            + $"produced {first.Length}, so the scrollback was lost.");
    }

    /// <summary>Restart is the one action that is meant to end it, and must really do so.</summary>
    [Fact]
    public async Task Restarting_ends_the_session()
    {
        if (PtySession.FindClaude() is null) Assert.Skip("The `claude` CLI is not on PATH.");

        using var http = new HttpClient { BaseAddress = new Uri(_server.Url) };

        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(PtyUri("sample"), TestContext.Current.CancellationToken);
            Assert.NotEqual(string.Empty, await DrainAsync(socket, 400, TimeSpan.FromSeconds(45)));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null,
                TestContext.Current.CancellationToken);
        }

        // Detaching left it running -- that is the behaviour the switch relies on.
        Assert.True(await IsRunningAsync(http));

        var response = await http.PostAsync("/api/projects/sample/terminal/restart", null,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        // Asserting on the session rather than on how much output a fresh CLI happens to
        // paint: a new Claude prints a banner immediately, so byte counts prove nothing.
        Assert.False(await IsRunningAsync(http));
    }

    private static async Task<bool> IsRunningAsync(HttpClient http)
    {
        var status = await http.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/api/projects/sample/terminal", TestContext.Current.CancellationToken);
        return status.GetProperty("running").GetBoolean();
    }
}
