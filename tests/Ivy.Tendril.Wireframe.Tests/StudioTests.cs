using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Ivy.Tendril.Wireframe.Studio.Projects;

namespace Ivy.Tendril.Wireframe.Tests;

public class StudioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wireframe-studio-tests", Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private WireframeProject Scaffold(string relative)
    {
        var project = WireframeProject.At(Path.Combine(_root, relative));
        new ProjectScaffolder(AssetCatalog.Default).Scaffold(project);
        return project;
    }

    [Fact]
    public void Finds_projects_nested_under_the_root()
    {
        Scaffold("alpha");
        Scaffold("beta");

        var names = new ProjectIndex(_root).List().Select(p => p.Name).ToList();

        Assert.Equal(2, names.Count);
        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
    }

    [Fact]
    public void Finds_the_root_itself_when_it_is_a_project()
    {
        // Pointing Studio at a single project has to work as well as pointing it at a
        // folder of them.
        var project = Scaffold(".");
        var found = new ProjectIndex(project.Root).List();

        Assert.Single(found);
        Assert.Equal(project.Root, found[0].Path);
    }

    [Fact]
    public void Ignores_directories_that_are_not_wireframe_projects()
    {
        Scaffold("real");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-project", "src"));
        File.WriteAllText(Path.Combine(_root, "not-a-project", "src", "index.ts"), "// no main.tsx");

        var found = new ProjectIndex(_root).List();

        Assert.Single(found);
        Assert.Equal("real", found[0].Name);
    }

    [Fact]
    public void Counts_source_files_and_screenshots()
    {
        var project = Scaffold("counted");
        File.WriteAllBytes(Path.Combine(project.ScreenshotsDir, "1440x900.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(project.ScreenshotsDir, "420x900.png"), [1, 2, 3]);

        var summary = new ProjectIndex(_root).List().Single();

        // index.html, App.tsx, main.tsx, wireframe-ready.ts -- the app is all under src/.
        Assert.Equal(4, summary.FileCount);
        Assert.Equal(2, summary.ScreenshotCount);
    }

    [Fact]
    public void App_tsx_sorts_first_so_the_editor_opens_the_wireframe()
    {
        var project = Scaffold("ordered");
        var files = ProjectIndex.SourceFiles(project).ToList();

        Assert.EndsWith("App.tsx", files[0].Path);
    }

    [Fact]
    public void Shot_dimensions_come_from_the_filename()
    {
        var project = Scaffold("shots");
        foreach (var name in new[] { "1440x900.png", "420x900.png", "1440xfull.png", "1440x900@3x.png" })
            File.WriteAllBytes(Path.Combine(project.ScreenshotsDir, name), [1]);

        var shots = ProjectIndex.Shots(project).ToDictionary(s => s.Name);

        Assert.Equal((1440, 900), (shots["1440x900.png"].Width, shots["1440x900.png"].Height));
        Assert.Equal((420, 900), (shots["420x900.png"].Width, shots["420x900.png"].Height));
        // A non-default scale keeps its CSS dimensions, not the pixel ones.
        Assert.Equal(1440, shots["1440x900@3x.png"].Width);
        // "full" has no height in the name; the gallery just needs a plausible ratio.
        Assert.Equal(1440, shots["1440xfull.png"].Width);
        Assert.True(shots["1440xfull.png"].Height > 0);
    }

    [Theory]
    [InlineData("src/App.tsx", true)]
    [InlineData("src/nested/Thing.tsx", true)]
    [InlineData("../../../etc/passwd", false)]
    [InlineData("src/../../outside.tsx", false)]
    [InlineData(".wireframe/tsconfig.base.json", false)]
    [InlineData("screenshots/1440x900.png", false)]
    [InlineData("", false)]
    public void Editing_is_confined_to_the_projects_src_directory(string relative, bool allowed)
    {
        // The editor and the agent both write through this. Anything outside src/ -- the
        // regenerated workspace, the screenshots, or a traversal out of the project -- has
        // to be refused.
        var project = Scaffold("guarded");
        var resolved = ProjectIndex.ResolveSourcePath(project, relative);

        Assert.Equal(allowed, resolved is not null);
    }

    [Fact]
    public void Newest_project_is_listed_first()
    {
        Scaffold("older");
        Thread.Sleep(20);
        var newer = Scaffold("newer");
        File.SetLastWriteTimeUtc(Path.Combine(newer.SourceDir, "App.tsx"), DateTime.UtcNow.AddMinutes(1));

        Assert.Equal("newer", new ProjectIndex(_root).List()[0].Name);
    }
}
