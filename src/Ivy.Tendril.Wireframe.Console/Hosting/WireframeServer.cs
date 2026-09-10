using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ivy.Tendril.Wireframe.Console.Hosting;

public sealed record ServerOptions(
    WireframeProject Project,
    string OutDir,
    bool LiveReload,
    int Port = 0);

/// <summary>
/// The dev server. Binds a free loopback port, serves the embedded payload plus the
/// freshly-built bundle, and (in serve mode) hosts the live-reload socket.
/// </summary>
public sealed class WireframeServer(AssetCatalog assets, ServerOptions options) : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly LiveReloadHub _hub = new();

    public LiveReloadHub Hub => _hub;
    public string Url { get; private set; } = "";

    /// <summary>Raised when the page reports an uncaught error, so serve can print it.</summary>
    public event Action<string, string>? PageError;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.AddSingleton(assets);

        builder.WebHost.ConfigureKestrel(k =>
        {
            // Listen(Loopback, 0), not ListenLocalhost(0): "localhost" means binding both
            // 127.0.0.1 and [::1], which with port 0 would allocate two DIFFERENT ephemeral
            // ports. Kestrel rejects that combination outright.
            //
            // Loopback-only is also deliberate -- unreleased wireframes have no business
            // being reachable from the network.
            k.Listen(IPAddress.Loopback, options.Port);
            k.AddServerHeader = false;
        });

        var app = builder.Build();
        Configure(app);

        await app.StartAsync(ct);
        _app = app;

        // The only race-free way to learn the port: read it back after StartAsync.
        // Probing with a TcpListener and closing it is a TOCTOU bug on Windows.
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not report a bound address.");

        // Report 127.0.0.1 rather than "localhost", which can resolve to ::1 first and fail.
        Url = address.Replace("[::]", "127.0.0.1").Replace("localhost", "127.0.0.1");
    }

    private void Configure(WebApplication app)
    {
        var indexBuilder = new IndexHtmlBuilder(VendorManifest.Load(assets));

        app.UseWebSockets();

        // Dev server: never cache anything.
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            ctx.Response.Headers.Pragma = "no-cache";
            await next();
        });

        app.Map("/__wireframe/hmr", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await _hub.AddAsync(socket, ctx.RequestAborted);
        });

        app.MapPost("/__wireframe/report", async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            try
            {
                using var doc = JsonDocument.Parse(body);
                PageError?.Invoke(
                    doc.RootElement.GetProperty("kind").GetString() ?? "error",
                    doc.RootElement.GetProperty("detail").GetString() ?? "");
            }
            catch (JsonException)
            {
                // A malformed beacon is not worth failing the request over.
            }
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        if (options.LiveReload)
        {
            app.MapGet("/__wireframe/client.js", async ctx =>
            {
                ctx.Response.ContentType = "text/javascript; charset=utf-8";
                await ctx.Response.WriteAsync(LiveReloadClient.Source);
            });
        }

        // Freshly-built bundle (on disk, outside the project).
        app.MapGet("/__wireframe/out/{**path}", async ctx =>
            await ServeFileAsync(ctx, options.OutDir, (string?)ctx.Request.RouteValues["path"]));

        // Embedded payload: vendor JS, stylesheets, fonts.
        foreach (var area in new[] { "vendor", "css", "fonts" })
        {
            var captured = area;
            app.MapGet($"/__wireframe/{captured}/{{**path}}", async ctx =>
            {
                var rel = (string?)ctx.Request.RouteValues["path"] ?? "";
                var key = $"{captured}/{rel}";
                if (!assets.TryRead(key, out var bytes))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                ctx.Response.ContentType = ContentTypeFor(rel);
                await ctx.Response.Body.WriteAsync(bytes);
            });
        }

        // The user's public/ directory.
        app.MapGet("/{**path}", async ctx =>
        {
            var rel = (string?)ctx.Request.RouteValues["path"] ?? "";

            if (rel.Length > 0 && await TryServeFileAsync(ctx, options.Project.PublicDir, rel))
                return;

            // A request WITH an extension that we could not satisfy is a genuine 404.
            // Returning index.html here is the classic SPA-server bug: the browser then
            // reports "Failed to load module script: MIME type text/html", which is one
            // of the most confusing errors in frontend development.
            if (rel.Length > 0 && Path.HasExtension(rel))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(indexBuilder.Build(options.Project, options.LiveReload));
        });
    }

    private static async Task ServeFileAsync(HttpContext ctx, string root, string? relative)
    {
        if (!await TryServeFileAsync(ctx, root, relative ?? ""))
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static async Task<bool> TryServeFileAsync(HttpContext ctx, string root, string relative)
    {
        if (string.IsNullOrEmpty(relative)) return false;

        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        // Containment check: a crafted path must not escape the served root.
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(full)) return false;

        ctx.Response.ContentType = ContentTypeFor(full);
        await ctx.Response.SendFileAsync(full);
        return true;
    }

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".html" => "text/html; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".ico" => "image/x-icon",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };

    public async ValueTask DisposeAsync()
    {
        await _hub.DisposeAsync();
        if (_app is not null) await _app.DisposeAsync();
    }
}

/// <summary>Fans build events out to every connected browser.</summary>
public sealed class LiveReloadHub : IAsyncDisposable
{
    private readonly List<WebSocket> _sockets = [];
    private readonly Lock _gate = new();

    public async Task AddAsync(WebSocket socket, CancellationToken ct)
    {
        lock (_gate) _sockets.Add(socket);
        try
        {
            // Hold the request open; we only ever push. Reading also lets us notice close.
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            // Browser navigated away or the server is shutting down.
        }
        finally
        {
            lock (_gate) _sockets.Remove(socket);
        }
    }

    public void Broadcast(object message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        WebSocket[] targets;
        lock (_gate) targets = [.. _sockets];

        foreach (var socket in targets)
        {
            if (socket.State != WebSocketState.Open) continue;
            _ = SendSafeAsync(socket, bytes);
        }
    }

    private static async Task SendSafeAsync(WebSocket socket, byte[] bytes)
    {
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The client vanished between the state check and the send.
        }
    }

    public async ValueTask DisposeAsync()
    {
        WebSocket[] targets;
        lock (_gate) { targets = [.. _sockets]; _sockets.Clear(); }

        foreach (var socket in targets)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", CancellationToken.None);
            }
            catch
            {
                // Best effort on shutdown.
            }
        }
    }
}
