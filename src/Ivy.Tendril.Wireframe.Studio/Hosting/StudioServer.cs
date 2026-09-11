using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Build;
using Ivy.Tendril.Wireframe.Console.Project;
using Ivy.Tendril.Wireframe.Console.Screenshot;
using Ivy.Tendril.Wireframe.Studio.Preview;
using Ivy.Tendril.Wireframe.Studio.Projects;
using Ivy.Tendril.Wireframe.Studio.Terminal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ivy.Tendril.Wireframe.Studio.Hosting;

/// <summary>
/// Hosts the Studio: a React + Tailwind SPA, a small JSON API over the projects on disk,
/// one SSE stream for pushes, and a relay to the agent CLI.
/// </summary>
public sealed class StudioServer(AssetCatalog assets, ProjectIndex index, int port = 0) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // The client is TypeScript; enums cross the wire as camelCase strings.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private WebApplication? _app;
    private readonly PreviewSupervisor _preview = new(assets);
    private readonly SseHub _events = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private string? _wireframeCli;

    public string Url { get; private set; } = "";

    public async Task StartAsync(CancellationToken ct = default)
    {
        _wireframeCli = ResolveWireframeCli();

        _preview.StatusChanged += (project, status) =>
            _events.Broadcast(new { type = "preview", project, status });
        _preview.Rebuilt += project =>
            _events.Broadcast(new { type = "files", project });

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port);
            k.AddServerHeader = false;
        });

        var app = builder.Build();
        Configure(app);

        await app.StartAsync(ct);
        _app = app;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not report a bound address.");
        Url = address.Replace("[::]", "127.0.0.1").Replace("localhost", "127.0.0.1");

        WatchRoot();
    }

    private void Configure(WebApplication app)
    {
        // Required for the agent terminal. Without it IsWebSocketRequest is always false
        // and the upgrade is rejected with a 400 before any handler runs.
        app.UseWebSockets();

        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            await next();
        });

        // ---- shell -------------------------------------------------------------
        app.MapGet("/", async ctx =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(IndexHtml());
        });

        app.MapGet("/studio/app.js", async ctx => await SendAssetAsync(ctx, "studio/app.js", "text/javascript"));
        app.MapGet("/studio/app.css", async ctx => await SendAssetAsync(ctx, "studio/app.css", "text/css"));

        // ---- projects ----------------------------------------------------------
        app.MapGet("/api/projects", () => Results.Json(index.List(), Json));

        app.MapGet("/api/projects/{name}/files", (string name) =>
            Resolve(name, project => Results.Json(ProjectIndex.SourceFiles(project).ToList(), Json)));

        app.MapGet("/api/projects/{name}/file", (string name, string path) =>
            Resolve(name, project =>
            {
                var full = ProjectIndex.ResolveSourcePath(project, path);
                if (full is null || !File.Exists(full)) return Results.NotFound();
                return Results.Text(File.ReadAllText(full), "text/plain; charset=utf-8");
            }));

        app.MapPut("/api/projects/{name}/file", async (string name, string path, HttpRequest request) =>
        {
            var project = index.Find(name);
            if (project is null) return Results.NotFound();

            var full = ProjectIndex.ResolveSourcePath(project, path);
            if (full is null) return Results.BadRequest("Path is outside src/.");

            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            await File.WriteAllTextAsync(full, await reader.ReadToEndAsync());

            // esbuild's watcher picks the change up and the wireframe's own live-reload
            // client refreshes the iframe, so nothing else is needed here.
            return Results.Ok();
        });

        // ---- screenshots -------------------------------------------------------
        app.MapGet("/api/projects/{name}/shots", (string name) =>
            Resolve(name, project => Results.Json(ProjectIndex.Shots(project).ToList(), Json)));

        app.MapGet("/api/projects/{name}/shots/{file}", (string name, string file) =>
            Resolve(name, project =>
            {
                // Screenshot names come from our own writer, but this endpoint is reachable
                // with anything, so keep it to a bare filename inside screenshots/.
                if (file.Contains('/') || file.Contains('\\') || file.Contains("..")) return Results.BadRequest();

                var full = Path.Combine(project.ScreenshotsDir, file);
                return File.Exists(full) ? Results.File(full, "image/png") : Results.NotFound();
            }));

        app.MapPost("/api/projects/{name}/shots", async (string name, CaptureRequest body, CancellationToken ct) =>
        {
            var project = index.Find(name);
            if (project is null) return Results.NotFound();

            var status = _preview.Status;
            if (_preview.CurrentProjectName != project.Name || status.Url is null)
                return Results.BadRequest("The preview is not running for this project.");

            var width = Math.Clamp(body.Width, 64, 8192);
            var height = Math.Clamp(body.Height, 64, 16384);
            var output = Path.Combine(project.ScreenshotsDir, $"{width}x{height}.png");

            // Capture against the already-running preview rather than standing up a second
            // server: it is the page the user is looking at, which is the whole point.
            var result = await new ScreenshotRunner().CaptureAsync(new ScreenshotOptions(
                status.Url, output, width, height,
                Scale: 2, FullPage: false, Transparent: false,
                Timeout: TimeSpan.FromSeconds(30), BrowserPath: null), ct);

            _events.Broadcast(new { type = "shots", project = project.Name });

            var info = new FileInfo(result.Path);
            return Results.Json(new ShotInfo(
                info.Name, width, height, info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)), Json);
        });

        // ---- preview -----------------------------------------------------------
        app.MapGet("/api/projects/{name}/preview", (string name) =>
            Resolve(name, project => Results.Json(
                _preview.CurrentProjectName == project.Name ? _preview.Status : PreviewStatus.Idle, Json)));

        app.MapPost("/api/projects/{name}/preview", async (string name, CancellationToken ct) =>
        {
            var project = index.Find(name);
            if (project is null) return Results.NotFound();
            return Results.Json(await _preview.OpenAsync(project, ct), Json);
        });

        app.MapPost("/api/projects/{name}/preview/rebuild", async (string name, CancellationToken ct) =>
        {
            var project = index.Find(name);
            if (project is null) return Results.NotFound();
            if (_preview.CurrentProjectName != project.Name)
                return Results.Json(await _preview.OpenAsync(project, ct), Json);
            return Results.Json(await _preview.RebuildAsync(ct), Json);
        });

        app.MapDelete("/api/projects/{name}", async (string name, CancellationToken ct) =>
        {
            var project = index.Find(name);
            if (project is null) return Results.NotFound();

            // Release the project first: the esbuild watcher holds handles inside it, and
            // on Windows an open handle makes the move fail.
            if (_preview.CurrentProjectName == project.Name) await _preview.CloseAsync(ct);

            var result = ProjectTrash.Trash(index, project);
            if (!result.Ok) return Results.Problem(result.Error, statusCode: 409);

            _events.Broadcast(new { type = "projects" });
            return Results.Json(new { trashedTo = result.Destination }, Json);
        });

        // ---- editor ------------------------------------------------------------
        app.MapGet("/api/editor", () => Results.Json(new { available = EditorLauncher.IsAvailable }, Json));

        // `int? line`, not `int`: a non-nullable value type bound from the query string is
        // treated as REQUIRED, so omitting it fails binding with a bare 400 before the
        // handler ever runs.
        app.MapPost("/api/projects/{name}/open-editor", (string name, string? path, int? line) =>
        {
            var project = index.Find(name);
            if (project is null) return Results.NotFound();

            // No path means "open the whole project", which is what you want when jumping
            // out to the editor to work on more than one file.
            string target;
            if (string.IsNullOrWhiteSpace(path))
            {
                target = project.Root;
            }
            else
            {
                var resolved = ProjectIndex.ResolveSourcePath(project, path);
                if (resolved is null) return Results.BadRequest("Path is outside src/.");
                target = resolved;
            }

            var result = EditorLauncher.Open(target, line ?? 0);
            return result.Ok ? Results.Ok() : Results.Problem(result.Error, statusCode: 409);
        });

        // ---- agent terminal ----------------------------------------------------
        // A real PTY rather than a chat API. Claude Code checks whether stdout is a TTY;
        // on a pipe it drops to non-interactive mode, so slash commands, plan mode and the
        // TUI would all be unavailable -- which is exactly what a terminal is for.
        app.Map("/api/projects/{name}/pty", async (string name, HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var project = index.Find(name);
            if (project is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await new TerminalBridge(project, _wireframeCli ?? "wireframe")
                .RunAsync(socket, ctx.RequestAborted);

            // The session almost certainly touched files and may have taken screenshots.
            _events.Broadcast(new { type = "files", project = project.Name });
            _events.Broadcast(new { type = "shots", project = project.Name });
        });

        app.MapGet("/api/terminal", () => Results.Json(new
        {
            available = PtySession.IsSupported && PtySession.FindClaude() is not null,
            reason = PtySession.FindClaude() is null
                ? "The `claude` CLI is not on PATH."
                : PtySession.UnsupportedReason,
        }, Json));

        // ---- events ------------------------------------------------------------
        app.MapGet("/api/events", async (HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.Connection = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await _events.SubscribeAsync(ctx.Response, ct);
        });
    }

    private IResult Resolve(string name, Func<WireframeProject, IResult> handler)
    {
        var project = index.Find(name);
        return project is null ? Results.NotFound() : handler(project);
    }

    private async Task SendAssetAsync(HttpContext ctx, string key, string contentType)
    {
        if (!assets.TryRead(key, out var bytes))
        {
            // The UI bundle lives in this assembly, not the Console's payload.
            var stream = typeof(StudioServer).Assembly.GetManifestResourceStream(key);
            if (stream is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        ctx.Response.ContentType = $"{contentType}; charset=utf-8";
        await ctx.Response.Body.WriteAsync(bytes);
    }

    /// <summary>
    /// Sets data-theme before the stylesheet paints. Without it a light-mode user sees a
    /// flash of the dark default while the bundle loads.
    ///
    /// Kept out of the interpolated index.html literal on purpose: inside a $"""..."""
    /// raw string, `{` opens an interpolation, so JS braces would have to be doubled and
    /// the script becomes unreadable.
    /// </summary>
    private const string EarlyThemeScript =
        """
        <script>
          try {
            var choice = localStorage.getItem("wireframe-studio-theme") || "system";
            var light = choice === "light" ||
              (choice === "system" && matchMedia("(prefers-color-scheme: light)").matches);
            document.documentElement.dataset.theme = light ? "light" : "dark";
          } catch (e) {
            document.documentElement.dataset.theme = "dark";
          }
        </script>
        """;

    private string IndexHtml() =>
        $"""
        <!doctype html>
        <html lang="en" class="h-full">
          <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>Wireframe Studio</title>
            <link rel="stylesheet" href="/studio/app.css" />
            <script>globalThis.__STUDIO_ROOT__ = {JsonSerializer.Serialize(index.Root)};</script>
            {EarlyThemeScript}
          </head>
          <body class="h-full">
            <div id="root" class="h-full"></div>
            <script type="module" src="/studio/app.js"></script>
          </body>
        </html>
        """;

    /// <summary>
    /// Watches the root for projects appearing or disappearing, and for screenshots landing
    /// (the agent takes those out of band, so nothing else would tell the UI).
    /// </summary>
    private void WatchRoot()
    {
        try
        {
            var watcher = new FileSystemWatcher(index.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };

            var debounce = new Debouncer(TimeSpan.FromMilliseconds(350));

            void OnChange(object _, FileSystemEventArgs e) => debounce.Run(() =>
            {
                var path = e.FullPath;

                if (path.Contains($"{Path.DirectorySeparatorChar}screenshots{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var project = FindOwningProject(path);
                    if (project is not null) _events.Broadcast(new { type = "shots", project });
                    return;
                }

                if (path.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var project = FindOwningProject(path);
                    if (project is not null) _events.Broadcast(new { type = "files", project });
                    return;
                }

                _events.Broadcast(new { type = "projects" });
            });

            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Renamed += OnChange;
            watcher.Changed += OnChange;

            _watchers.Add(watcher);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without a watcher the UI still works; the rail just needs a manual rescan.
        }
    }

    private string? FindOwningProject(string path) =>
        index.List().FirstOrDefault(p =>
            path.StartsWith(p.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>
    /// Locates the `wireframe` executable to hand the agent. Prefers the one shipped beside
    /// Studio so a dev build drives the dev CLI rather than whatever is installed globally.
    /// </summary>
    private static string ResolveWireframeCli()
    {
        var exe = OperatingSystem.IsWindows() ? "wireframe.exe" : "wireframe";
        var beside = Path.Combine(AppContext.BaseDirectory, exe);
        return File.Exists(beside) ? beside : "wireframe";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        await _preview.DisposeAsync();
        _events.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    private sealed record CaptureRequest(int Width, int Height);
}

/// <summary>Trailing-edge debounce: collapses a burst of filesystem events into one.</summary>
internal sealed class Debouncer(TimeSpan delay)
{
    private long _generation;

    public void Run(Action action)
    {
        var mine = Interlocked.Increment(ref _generation);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            if (Interlocked.Read(ref _generation) != mine) return;
            try { action(); } catch { /* a dropped notification is not fatal */ }
        });
    }
}

/// <summary>Fans JSON events out to every connected browser over SSE.</summary>
internal sealed class SseHub : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Channel> _clients = new();

    private sealed class Channel
    {
        public readonly System.Threading.Channels.Channel<string> Queue =
            System.Threading.Channels.Channel.CreateUnbounded<string>();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public void Broadcast(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Options);
        foreach (var client in _clients.Values) client.Queue.Writer.TryWrite(json);
    }

    public async Task SubscribeAsync(HttpResponse response, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = new Channel();
        _clients[id] = channel;

        try
        {
            // An immediate comment frame makes the browser consider the stream open.
            await response.WriteAsync(": connected\n\n", ct);
            await response.Body.FlushAsync(ct);

            await foreach (var json in channel.Queue.Reader.ReadAllAsync(ct))
            {
                await response.WriteAsync($"data: {json}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            // Browser went away.
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Queue.Writer.TryComplete();
        _clients.Clear();
    }
}
