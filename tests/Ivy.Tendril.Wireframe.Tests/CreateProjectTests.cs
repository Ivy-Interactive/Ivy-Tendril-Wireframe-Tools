using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Studio.Hosting;
using Ivy.Tendril.Wireframe.Studio.Projects;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// Creating a wireframe from the Studio. The name arrives from a browser field, so the
/// sanitising is as much a part of the feature as the scaffolding.
/// </summary>
public class CreateProjectTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wireframe-create-tests", Guid.NewGuid().ToString("N")[..8]);

    private StudioServer _server = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
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

    [Fact]
    public async Task Creates_a_project_that_is_immediately_usable()
    {
        var response = await _http.PostAsJsonAsync("/api/projects", new { name = "checkout-flow" },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal("checkout-flow", created.GetProperty("name").GetString());

        // Scaffolded, not just an empty directory -- serve and screenshot must accept it.
        var path = Path.Combine(_root, "checkout-flow");
        Assert.True(File.Exists(Path.Combine(path, "src", "main.tsx")));
        Assert.True(File.Exists(Path.Combine(path, "src", "App.tsx")));
        Assert.True(File.Exists(Path.Combine(path, "src", "index.html")));

        // And it shows up in the listing straight away.
        var projects = await _http.GetFromJsonAsync<List<JsonElement>>("/api/projects",
            TestContext.Current.CancellationToken);
        Assert.Contains(projects!, p => p.GetProperty("name").GetString() == "checkout-flow");
    }

    [Fact]
    public async Task Refuses_a_name_that_already_exists()
    {
        await _http.PostAsJsonAsync("/api/projects", new { name = "dupe" },
            TestContext.Current.CancellationToken);

        var second = await _http.PostAsJsonAsync("/api/projects", new { name = "dupe" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already exists",
            await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/name")]
    [InlineData(@"back\slash")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuses_a_name_that_would_escape_the_root_or_hide_the_project(string name)
    {
        var response = await _http.PostAsJsonAsync("/api/projects", new { name },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Nothing should have been written anywhere.
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    [Theory]
    [InlineData("checkout", "checkout")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("with space", "with space")]
    public void Accepts_ordinary_names(string input, string expected) =>
        Assert.Equal(expected, ProjectIndex.SanitizeName(input));

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData(null)]
    public void Rejects_unsafe_names(string? input) =>
        Assert.Null(ProjectIndex.SanitizeName(input));
}
