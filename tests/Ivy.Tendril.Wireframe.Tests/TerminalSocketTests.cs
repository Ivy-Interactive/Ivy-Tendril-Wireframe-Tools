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
}
