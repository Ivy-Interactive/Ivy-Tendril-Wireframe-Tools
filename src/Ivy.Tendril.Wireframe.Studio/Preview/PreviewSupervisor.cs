using System.Text.Json.Serialization;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Build;
using Ivy.Tendril.Wireframe.Console.Hosting;
using Ivy.Tendril.Wireframe.Console.Project;

namespace Ivy.Tendril.Wireframe.Studio.Preview;

// Serialized as camelCase ("running", not "Running") by the converter configured on
// StudioServer's JsonSerializerOptions. Deliberately not an attribute here: a converter
// attribute on the type wins over the options, and would reintroduce PascalCase.
public enum PreviewPhase
{
    Idle,
    Building,
    Starting,
    Running,
    Failed,
}

public sealed record PreviewStatus(
    [property: JsonPropertyName("phase")] PreviewPhase Phase,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("generation")] int Generation,
    [property: JsonPropertyName("message")] string? Message)
{
    public static readonly PreviewStatus Idle = new(PreviewPhase.Idle, null, 0, null);
}

/// <summary>
/// Keeps exactly one live wireframe server running, pointed at whichever project the
/// browser has selected.
///
/// The preview is a real dev server on its own loopback port, shown in an iframe — not
/// something rendered inside the Studio page. That separation is what makes the preview
/// honest: the wireframe gets its own document, its own React instance from the vendor
/// bundle, and the same esbuild watch pipeline `wireframe serve` uses, so what you see is
/// what `screenshot` will capture.
/// </summary>
public sealed class PreviewSupervisor(AssetCatalog assets) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WireframeProject? _project;
    private EsbuildWatcher? _watcher;
    private WireframeServer? _server;
    private string? _esbuild;
    private int _generation;

    public PreviewStatus Status { get; private set; } = PreviewStatus.Idle;

    /// <summary>Raised whenever <see cref="Status"/> changes, so the SSE stream can push it.</summary>
    public event Action<string, PreviewStatus>? StatusChanged;

    /// <summary>Raised when a rebuild finishes, so the UI can refresh the file list.</summary>
    public event Action<string>? Rebuilt;

    public string? CurrentProjectName => _project?.Name;

    private void Publish(PreviewStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(_project?.Name ?? "", status);
    }

    /// <summary>
    /// Points the preview at <paramref name="project"/>, tearing down the previous one.
    /// Selecting the already-running project is a no-op so clicking around the rail does
    /// not restart the server underneath you.
    /// </summary>
    public async Task<PreviewStatus> OpenAsync(WireframeProject project, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_project is not null
                && string.Equals(_project.Root, project.Root, StringComparison.OrdinalIgnoreCase)
                && Status.Phase == PreviewPhase.Running)
            {
                return Status;
            }

            await TeardownAsync();
            _project = project;
            return await StartAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the preview and releases the project's files. Must be called before the
    /// project directory is moved or deleted: the esbuild watcher holds handles inside it,
    /// and on Windows that makes the move fail.
    /// </summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await TeardownAsync();
            _project = null;
            Publish(PreviewStatus.Idle);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces a clean rebuild and restart of the current project.</summary>
    public async Task<PreviewStatus> RebuildAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_project is null) return Status;
            await TeardownAsync();
            return await StartAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PreviewStatus> StartAsync(CancellationToken ct)
    {
        var project = _project!;
        Publish(new PreviewStatus(PreviewPhase.Building, null, _generation, null));

        try
        {
            // A tool upgrade leaves .wireframe/ holding the previous payload's types.
            var scaffolder = new ProjectScaffolder(assets);
            if (scaffolder.NeedsRefresh(project)) scaffolder.MaterializeWorkspace(project);

            _esbuild ??= await new EsbuildProvisioner().ResolveAsync(ct);
            var vendor = VendorManifest.Load(assets);
            var outDir = project.OutDir("studio");

            _watcher = new EsbuildWatcher(_esbuild, project, vendor);
            _watcher.BuildCompleted += OnBuildCompleted;

            var first = await _watcher.StartAsync(outDir, ct);

            Publish(new PreviewStatus(PreviewPhase.Starting, null, _generation, null));

            _server = new WireframeServer(assets, new ServerOptions(project, outDir, LiveReload: true));
            await _server.StartAsync(ct);

            _generation++;

            var status = first.Success
                ? new PreviewStatus(PreviewPhase.Running, _server.Url, _generation, null)
                : new PreviewStatus(PreviewPhase.Failed, _server.Url, _generation, first.Output);

            Publish(status);
            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var status = new PreviewStatus(PreviewPhase.Failed, null, _generation, ex.Message);
            Publish(status);
            return status;
        }
    }

    /// <summary>
    /// The wireframe's own live-reload client handles the in-page refresh, so a successful
    /// rebuild must NOT bump the generation — remounting the iframe here would fight it and
    /// throw away the preview's scroll position on every keystroke.
    ///
    /// But that client only refreshes if something tells it to. `serve` broadcasts on the
    /// same event; without the equivalent here nothing in Studio hot-reloaded at all — an
    /// agent's edit, or a Ctrl+S in the code pane, rebuilt in milliseconds and then sat
    /// there invisible until the preview was reloaded by hand.
    /// </summary>
    private void OnBuildCompleted(BuildResult result)
    {
        var name = _project?.Name;
        if (name is null) return;

        Publish(result.Success
            ? new PreviewStatus(PreviewPhase.Running, _server?.Url, _generation, null)
            : new PreviewStatus(PreviewPhase.Failed, _server?.Url, _generation, result.Output));

        // Null during the very first build: the server is created after the watcher, and a
        // page that has not loaded yet has nothing to refresh.
        if (_server is not null)
        {
            if (result.Success) _server.Hub.Broadcast(new { type = "reload" });
            else _server.Hub.Broadcast(new
            {
                type = "error",
                text = result.Output,
                location = result.FirstLocation,
            });
        }

        Rebuilt?.Invoke(name);
    }

    private async Task TeardownAsync()
    {
        if (_watcher is not null)
        {
            _watcher.BuildCompleted -= OnBuildCompleted;
            await _watcher.DisposeAsync();
            _watcher = null;
        }
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await TeardownAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
