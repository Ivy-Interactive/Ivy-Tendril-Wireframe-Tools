using System.Text;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Ivy.Tendril.Wireframe.Studio.Hosting;
using Ivy.Tendril.Wireframe.Studio.Projects;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// What the root watcher pushes over SSE.
///
/// The watcher debounces, and it used to do so with one debouncer for everything --
/// last-writer-wins across every kind of change. So a suggestion landing while the agent
/// was editing src/, which is the only time a suggestion is ever written, was cancelled by
/// the src write that followed it and never reached the browser. It only showed up on a
/// manual reload.
/// </summary>
public class WatcherEventTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wireframe-watcher-tests", Guid.NewGuid().ToString("N")[..8]);

    private WireframeProject _project = null!;
    private StudioServer _server = null!;

    public async ValueTask InitializeAsync()
    {
        _project = WireframeProject.At(Path.Combine(_root, "sample"));
        new ProjectScaffolder(AssetCatalog.Default).Scaffold(_project);
        Directory.CreateDirectory(_project.SuggestionsDir);
        Directory.CreateDirectory(_project.ScreenshotsDir);

        _server = new StudioServer(AssetCatalog.Default, new ProjectIndex(_root));
        await _server.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        TempRoot.Remove(_root);
    }

    [Fact]
    public async Task A_suggestion_is_pushed_even_while_src_is_being_written()
    {
        var ct = TestContext.Current.CancellationToken;
        using var events = await SseReader.ConnectAsync(_server.Url, ct);

        // The realistic shape: the agent is editing the wireframe and drops a suggestion in
        // the middle of it. Every one of these lands inside one 350ms debounce window.
        for (var i = 0; i < 3; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_project.SourceDir, "App.tsx"), $"// pass {i}\nexport default function App() {{ return <div />; }}", ct);
            await Task.Delay(30, ct);
        }

        await File.WriteAllTextAsync(
            Path.Combine(_project.SuggestionsDir, "badge.html"), "<title>Badge stretches</title>", ct);

        for (var i = 0; i < 3; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_project.SourceDir, "App.tsx"), $"// pass {i + 3}\nexport default function App() {{ return <div />; }}", ct);
            await Task.Delay(30, ct);
        }

        var suggestion = await events.WaitForAsync("\"type\":\"suggestions\"", TimeSpan.FromSeconds(10), ct);

        Assert.True(suggestion,
            "No suggestions event arrived. A src write cancelled it, so the tab only fills in "
            + $"on a manual reload. Frames seen: {events.Transcript}");
    }

    [Fact]
    public async Task Two_kinds_changing_together_both_arrive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var events = await SseReader.ConnectAsync(_server.Url, ct);

        await File.WriteAllTextAsync(
            Path.Combine(_project.SuggestionsDir, "note.html"), "<title>Note</title>", ct);
        await File.WriteAllBytesAsync(
            Path.Combine(_project.ScreenshotsDir, "1440x900.png"), [0x89, 0x50, 0x4E, 0x47], ct);

        Assert.True(await events.WaitForAsync("\"type\":\"suggestions\"", TimeSpan.FromSeconds(10), ct),
            $"No suggestions event. Frames seen: {events.Transcript}");
        Assert.True(await events.WaitForAsync("\"type\":\"shots\"", TimeSpan.FromSeconds(10), ct),
            $"No shots event. Frames seen: {events.Transcript}");
    }
}

/// <summary>Reads the Studio's SSE stream, keeping everything it has seen so a failure can
/// say what did arrive instead.</summary>
internal sealed class SseReader : IDisposable
{
    private readonly HttpClient _http;
    private readonly StreamReader _reader;
    private readonly StringBuilder _seen = new();

    private SseReader(HttpClient http, StreamReader reader)
    {
        _http = http;
        _reader = reader;
    }

    public string Transcript => _seen.Length == 0 ? "(none)" : _seen.ToString();

    public static async Task<SseReader> ConnectAsync(string url, CancellationToken ct)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var response = await http.GetAsync(
            $"{url}/api/events", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        return new SseReader(http, new StreamReader(await response.Content.ReadAsStreamAsync(ct)));
    }

    /// <summary>True once a frame containing <paramref name="fragment"/> has arrived. Frames
    /// seen while waiting are kept, including ones matched by an earlier call.</summary>
    public async Task<bool> WaitForAsync(string fragment, TimeSpan patience, CancellationToken ct)
    {
        if (_seen.ToString().Contains(fragment, StringComparison.Ordinal)) return true;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(patience);

        try
        {
            while (await _reader.ReadLineAsync(deadline.Token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                _seen.Append(line).Append(' ');
                if (line.Contains(fragment, StringComparison.Ordinal)) return true;
            }
        }
        catch (OperationCanceledException)
        {
            // Ran out of patience; the caller turns that into a readable failure.
        }

        return false;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _http.Dispose();
    }
}
