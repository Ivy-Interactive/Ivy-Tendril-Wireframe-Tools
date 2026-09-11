using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Ivy.Tendril.Wireframe.Studio.Hosting;
using Ivy.Tendril.Wireframe.Studio.Projects;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// The agent's suggestions: pages it writes into &lt;project&gt;/suggestions/ when something
/// would have made the build easier.
///
/// The filename reaches the server from a folder the agent writes, so unlike the screenshot
/// endpoints this one takes genuinely untrusted input and the path guards get tests.
/// </summary>
public class SuggestionsTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wireframe-suggestion-tests", Guid.NewGuid().ToString("N")[..8]);

    private WireframeProject _project = null!;
    private StudioServer _server = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _project = WireframeProject.At(Path.Combine(_root, "sample"));
        new ProjectScaffolder(AssetCatalog.Default).Scaffold(_project);

        _server = new StudioServer(AssetCatalog.Default, new ProjectIndex(_root));
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_server.Url) };
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_server is not null) await _server.DisposeAsync();
        TempRoot.Remove(_root);
    }

    private string Write(string name, string html)
    {
        Directory.CreateDirectory(_project.SuggestionsDir);
        var path = Path.Combine(_project.SuggestionsDir, name);
        File.WriteAllText(path, html);
        return path;
    }

    [Fact]
    public void A_project_with_no_suggestions_folder_lists_none() =>
        Assert.Empty(ProjectIndex.Suggestions(_project));

    [Fact]
    public void The_list_shows_the_document_title()
    {
        Write("timeline.html", "<!doctype html><html><head><title>Needs a Timeline</title></head><body>…</body></html>");

        var listed = Assert.Single(ProjectIndex.Suggestions(_project));

        Assert.Equal("timeline.html", listed.Name);
        Assert.Equal("Needs a Timeline", listed.Title);
    }

    [Theory]
    [InlineData("<html><head><title>  Spaced  </title></head></html>", "Spaced")]
    [InlineData("<title lang=\"en\">With an attribute</title>", "With an attribute")]
    [InlineData("<title>Callout &amp; Banner</title>", "Callout & Banner")]
    public void Titles_are_read_leniently(string html, string expected)
    {
        Write("note.html", html);
        Assert.Equal(expected, ProjectIndex.Suggestions(_project).Single().Title);
    }

    [Theory]
    [InlineData("<html><body>No title here</body></html>")]
    [InlineData("<title>unterminated")]
    [InlineData("<title></title>")]
    public void A_page_with_no_usable_title_falls_back_to_the_filename(string html)
    {
        Write("note.html", html);
        Assert.Equal("note.html", ProjectIndex.Suggestions(_project).Single().Title);
    }

    [Fact]
    public void Only_html_is_listed()
    {
        Write("keep.html", "<title>Keep</title>");
        Write("notes.txt", "not a page");
        Write("shot.png", "not a page either");

        Assert.Equal(["keep.html"], ProjectIndex.Suggestions(_project).Select(s => s.Name));
    }

    [Fact]
    public async Task The_api_lists_and_serves_them()
    {
        Write("prop.html", "<!doctype html><title>Callout needs a density</title><p>body</p>");

        var listed = await _http.GetFromJsonAsync<List<JsonElement>>(
            "/api/projects/sample/suggestions", TestContext.Current.CancellationToken);

        Assert.Equal("Callout needs a density", listed!.Single().GetProperty("title").GetString());

        var page = await _http.GetAsync("/api/projects/sample/suggestions/prop.html",
            TestContext.Current.CancellationToken);
        page.EnsureSuccessStatusCode();

        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Callout needs a density",
            await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_project_summary_counts_them()
    {
        Write("one.html", "<title>One</title>");
        Write("two.html", "<title>Two</title>");

        var projects = await _http.GetFromJsonAsync<List<JsonElement>>(
            "/api/projects", TestContext.Current.CancellationToken);

        Assert.Equal(2, projects!.Single().GetProperty("suggestionCount").GetInt32());
    }

    [Theory]
    [InlineData("../../../../windows/win.ini")]
    [InlineData("..%2Fescape.html")]
    [InlineData("nested/page.html")]
    [InlineData("notes.txt")]
    [InlineData("main.tsx")]
    public async Task The_file_endpoint_refuses_anything_but_a_bare_html_name(string file)
    {
        var response = await _http.GetAsync($"/api/projects/sample/suggestions/{file}",
            TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"'{file}' answered {(int)response.StatusCode}; it must not be served.");
    }

    [Fact]
    public async Task Deleting_removes_the_file()
    {
        var path = Write("stale.html", "<title>Already done</title>");

        var response = await _http.DeleteAsync("/api/projects/sample/suggestions/stale.html",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        Assert.False(File.Exists(path));
        Assert.Empty(ProjectIndex.Suggestions(_project));
    }

    [Fact]
    public async Task Deleting_refuses_to_reach_outside_the_folder()
    {
        // Sits in the project but outside suggestions/, so the guard is the only thing
        // between the endpoint and a source file.
        var appFile = Path.Combine(_project.SourceDir, "App.tsx");

        var response = await _http.DeleteAsync(
            "/api/projects/sample/suggestions/..%2F..%2Fsrc%2FApp.tsx", TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(appFile), "The delete endpoint escaped suggestions/.");
    }
}
