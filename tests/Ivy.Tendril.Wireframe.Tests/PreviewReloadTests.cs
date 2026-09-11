using System.Net.WebSockets;
using System.Text;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Ivy.Tendril.Wireframe.Studio.Preview;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// Hot reload inside Studio.
///
/// `serve` broadcasts a reload on every successful rebuild; the Studio preview did not,
/// so an edit -- from the code pane or from the agent -- rebuilt in milliseconds and then
/// sat there invisible until the preview was reloaded by hand. The rebuild was never the
/// problem, which is why this is asserted on the wire rather than on the watcher.
/// </summary>
public class PreviewReloadTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wireframe-reload-tests", Guid.NewGuid().ToString("N")[..8]);

    private WireframeProject _project = null!;
    private PreviewSupervisor _preview = null!;

    public ValueTask InitializeAsync()
    {
        _project = WireframeProject.At(Path.Combine(_root, "sample"));
        new ProjectScaffolder(AssetCatalog.Default).Scaffold(_project);
        _preview = new PreviewSupervisor(AssetCatalog.Default);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_preview is not null) await _preview.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task An_edit_on_disk_pushes_a_reload_to_the_preview()
    {
        var ct = TestContext.Current.CancellationToken;

        var status = await _preview.OpenAsync(_project, ct);
        Assert.Equal(PreviewPhase.Running, status.Phase);
        Assert.NotNull(status.Url);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(status.Url!.Replace("http://", "ws://") + "/__wireframe/hmr"), ct);

        // What the agent does: write the file. Not a Studio API call -- Claude Code edits
        // the project directly, so the watcher is the only thing that can notice.
        var app = Path.Combine(_project.SourceDir, "App.tsx");
        var source = await File.ReadAllTextAsync(app, ct);
        await File.WriteAllTextAsync(
            app, source.Replace("<div />", "<div id=\"edited\" />", StringComparison.Ordinal), ct);

        var message = await ReceiveAsync(socket, "reload", TimeSpan.FromSeconds(30), ct);

        Assert.Contains("reload", message);
    }

    [Fact]
    public async Task A_broken_edit_pushes_the_error_instead()
    {
        var ct = TestContext.Current.CancellationToken;

        var status = await _preview.OpenAsync(_project, ct);
        Assert.Equal(PreviewPhase.Running, status.Phase);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(status.Url!.Replace("http://", "ws://") + "/__wireframe/hmr"), ct);

        var app = Path.Combine(_project.SourceDir, "App.tsx");
        await File.WriteAllTextAsync(app, "export default function App() { return ( <div> }", ct);

        var message = await ReceiveAsync(socket, "error", TimeSpan.FromSeconds(30), ct);

        // The overlay needs the diagnostic text, not just the fact that something broke.
        Assert.Contains("error", message);
        Assert.Contains("App.tsx", message);
    }

    /// <summary>Reads frames until one mentions <paramref name="expected"/>, so an unrelated
    /// message in between does not fail the test.</summary>
    private static async Task<string> ReceiveAsync(
        ClientWebSocket socket, string expected, TimeSpan patience, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var deadline = DateTime.UtcNow + patience;
        var seen = new StringBuilder();

        while (DateTime.UtcNow < deadline)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(deadline - DateTime.UtcNow);

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

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            seen.AppendLine(text);
            if (text.Contains(expected, StringComparison.Ordinal)) return text;
        }

        Assert.Fail(
            $"No '{expected}' message arrived within {patience.TotalSeconds:0}s, so the edit never "
            + $"reached the preview. Frames seen: {(seen.Length == 0 ? "(none)" : seen.ToString())}");
        return string.Empty;
    }
}
