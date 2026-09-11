using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Ivy.Tendril.Wireframe.Studio.Hosting;
using Ivy.Tendril.Wireframe.Studio.Projects;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// Boots the real Studio server and exercises its JSON API over HTTP.
///
/// These exist because of a bug that no unit test could have caught: `open-editor` declared
/// its optional line number as `int` rather than `int?`, and ASP.NET Core treats a
/// non-nullable value type from the query string as REQUIRED -- so omitting it failed
/// binding with a bare 400 before the handler ran. Every optional query parameter here is
/// now called without it at least once.
/// </summary>
public class StudioApiTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wireframe-api-tests", Guid.NewGuid().ToString("N")[..10]);

    private StudioServer _server = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var project = WireframeProject.At(Path.Combine(_root, "sample"));
        new ProjectScaffolder(AssetCatalog.Default).Scaffold(project);
        File.WriteAllBytes(Path.Combine(project.ScreenshotsDir, "1440x900.png"), [0x89, 0x50, 0x4E, 0x47]);

        _server = new StudioServer(AssetCatalog.Default, new ProjectIndex(_root));
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_server.Url) };
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_server is not null) await _server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Serves_the_studio_shell_and_its_bundle()
    {
        var html = await _http.GetAsync("/");
        html.EnsureSuccessStatusCode();
        var body = await html.Content.ReadAsStringAsync();

        Assert.Contains("/studio/app.js", body);
        Assert.Contains("/studio/app.css", body);
        // The scanned root is stamped in so the UI can show it without another round trip.
        Assert.Contains("__STUDIO_ROOT__", body);

        Assert.True((await _http.GetAsync("/studio/app.js")).IsSuccessStatusCode);
        Assert.True((await _http.GetAsync("/studio/app.css")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Lists_projects_with_their_counts()
    {
        var projects = await _http.GetFromJsonAsync<List<JsonElement>>("/api/projects");

        var project = Assert.Single(projects!);
        Assert.Equal("sample", project.GetProperty("name").GetString());
        Assert.Equal(3, project.GetProperty("fileCount").GetInt32());
        Assert.Equal(1, project.GetProperty("screenshotCount").GetInt32());
    }

    [Fact]
    public async Task Preview_phase_crosses_the_wire_as_camelCase()
    {
        // The TypeScript client switches on "running"; PascalCase would silently fall
        // through to "no preview" and the iframe would never appear.
        var body = await _http.GetStringAsync("/api/projects/sample/preview");
        Assert.Contains("\"phase\":\"idle\"", body);
    }

    [Fact]
    public async Task Reads_and_writes_a_source_file()
    {
        var original = await _http.GetStringAsync("/api/projects/sample/file?path=src/App.tsx");
        Assert.Contains("tendril-wireframes", original);

        var edited = original + "\n// touched by a test\n";
        var put = await _http.PutAsync(
            "/api/projects/sample/file?path=src/App.tsx", new StringContent(edited));
        put.EnsureSuccessStatusCode();

        Assert.Contains("touched by a test",
            await _http.GetStringAsync("/api/projects/sample/file?path=src/App.tsx"));
    }

    [Theory]
    [InlineData("../../escape.txt")]
    [InlineData(".wireframe/tsconfig.base.json")]
    [InlineData("screenshots/1440x900.png")]
    public async Task Refuses_reads_and_writes_outside_src(string path)
    {
        var get = await _http.GetAsync($"/api/projects/sample/file?path={Uri.EscapeDataString(path)}");
        Assert.False(get.IsSuccessStatusCode);

        var put = await _http.PutAsync(
            $"/api/projects/sample/file?path={Uri.EscapeDataString(path)}", new StringContent("x"));
        Assert.False(put.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Serves_screenshots_and_refuses_traversal()
    {
        var shots = await _http.GetFromJsonAsync<List<JsonElement>>("/api/projects/sample/shots");
        Assert.Single(shots!);

        Assert.True((await _http.GetAsync("/api/projects/sample/shots/1440x900.png")).IsSuccessStatusCode);
        Assert.False((await _http.GetAsync("/api/projects/sample/shots/..%2F..%2FApp.tsx")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Open_editor_binds_without_the_optional_line_parameter()
    {
        // The regression this whole file exists for. Whether an editor is installed decides
        // between 200 and 409 -- but 400 means the request never reached the handler, which
        // is the bug.
        foreach (var url in new[]
        {
            "/api/projects/sample/open-editor",
            "/api/projects/sample/open-editor?path=src%2FApp.tsx",
        })
        {
            var response = await _http.PostAsync(url, null);
            Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Open_editor_still_refuses_a_path_outside_src()
    {
        var response = await _http.PostAsync(
            "/api/projects/sample/open-editor?path=..%2F..%2Fsecrets.txt", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("outside src", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_project_is_a_404_everywhere()
    {
        foreach (var url in new[]
        {
            "/api/projects/nope/files",
            "/api/projects/nope/shots",
            "/api/projects/nope/preview",
            "/api/projects/nope/file?path=src/App.tsx",
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.DeleteAsync("/api/projects/nope")).StatusCode);
    }

    [Fact]
    public async Task Reports_whether_an_editor_was_found()
    {
        // Environment-dependent, so assert the shape rather than the value.
        var body = await _http.GetStringAsync("/api/editor");
        Assert.Contains("\"available\":", body);
    }
}
