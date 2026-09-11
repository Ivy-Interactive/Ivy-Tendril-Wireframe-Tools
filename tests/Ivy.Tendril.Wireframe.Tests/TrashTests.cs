using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Ivy.Tendril.Wireframe.Studio.Projects;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// Deleting is the one destructive thing Studio does, so the guards get their own tests.
/// </summary>
public class TrashTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wireframe-trash-tests", Guid.NewGuid().ToString("N")[..10]);

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
    public void Trashing_moves_the_project_rather_than_deleting_it()
    {
        var project = Scaffold("doomed");
        var index = new ProjectIndex(_root);

        var result = ProjectTrash.Trash(index, project);

        Assert.True(result.Ok, result.Error);
        Assert.False(Directory.Exists(project.Root));

        // The whole point: it is still on disk and recoverable with one move.
        Assert.NotNull(result.Destination);
        Assert.True(File.Exists(Path.Combine(result.Destination!, "src", "App.tsx")));
        Assert.Contains(ProjectTrash.TrashDirectoryName, result.Destination);
    }

    [Fact]
    public void Trashed_projects_disappear_from_the_listing()
    {
        Scaffold("keep");
        var doomed = Scaffold("doomed");
        var index = new ProjectIndex(_root);

        ProjectTrash.Trash(index, doomed);

        // .trash starts with a dot, which the scan skips -- so it must not come back as a
        // project, or deleting one would just rename it in the rail.
        var names = index.List().Select(p => p.Name).ToList();
        Assert.Equal(["keep"], names);
    }

    [Fact]
    public void The_scanned_root_cannot_be_trashed()
    {
        // In single-project mode the root IS the project. Trashing it would leave Studio
        // pointing at nothing, and is almost always a misclick.
        var project = Scaffold(".");
        var result = ProjectTrash.Trash(new ProjectIndex(project.Root), project);

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(project.SourceDir));
    }

    [Fact]
    public void A_project_outside_the_scanned_root_cannot_be_trashed()
    {
        var outside = Scaffold("outside");
        var inside = Path.Combine(_root, "scanned");
        Directory.CreateDirectory(inside);

        var result = ProjectTrash.Trash(new ProjectIndex(inside), outside);

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(outside.SourceDir));
    }

    [Fact]
    public void A_directory_that_is_not_a_wireframe_cannot_be_trashed()
    {
        // Guards against a mis-resolved name taking out an unrelated folder.
        var notAProject = Path.Combine(_root, "docs");
        Directory.CreateDirectory(notAProject);
        File.WriteAllText(Path.Combine(notAProject, "notes.md"), "important");

        var result = ProjectTrash.Trash(new ProjectIndex(_root), WireframeProject.At(notAProject));

        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(notAProject, "notes.md")));
    }

    [Fact]
    public void Trashing_the_same_name_twice_does_not_collide()
    {
        var index = new ProjectIndex(_root);

        var first = ProjectTrash.Trash(index, Scaffold("twice"));
        var second = ProjectTrash.Trash(index, Scaffold("twice"));

        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        Assert.NotEqual(first.Destination, second.Destination);
        Assert.True(Directory.Exists(first.Destination!));
        Assert.True(Directory.Exists(second.Destination!));
    }

    /// <summary>
    /// Deleting a wireframe that had a live agent used to fail outright: killing the PTY
    /// does not release the directory synchronously, so the move hit "being used by another
    /// process". A held file handle reproduces that on Windows; releasing it mid-flight is
    /// what the retry is there to survive.
    /// </summary>
    [Fact]
    public async Task Trashing_waits_for_a_lingering_handle_to_close()
    {
        var project = Scaffold("busy");
        var index = new ProjectIndex(_root);

        var held = new FileStream(
            Path.Combine(project.SourceDir, "App.tsx"),
            FileMode.Open, FileAccess.Read, FileShare.Read);

        // Prove the premise before testing the remedy: with the handle held, a single
        // attempt really does fail, and it fails in the retryable way.
        var blocked = ProjectTrash.Trash(index, project);
        Assert.False(blocked.Ok);
        Assert.True(blocked.Retryable, blocked.Error);

        // Let go shortly after the next attempt would have failed.
        var release = Task.Run(async () =>
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            await held.DisposeAsync();
        });

        var result = await ProjectTrash.TrashAsync(index, project, TestContext.Current.CancellationToken);
        await release;

        Assert.True(result.Ok, result.Error);
        Assert.False(Directory.Exists(project.Root));
    }

    /// <summary>A refusal is not retried -- waiting three seconds to say "that is not a
    /// wireframe" would just make the UI feel broken.</summary>
    [Fact]
    public async Task A_refusal_comes_back_immediately()
    {
        var notAProject = Path.Combine(_root, "plain");
        Directory.CreateDirectory(notAProject);

        var started = DateTime.UtcNow;
        var result = await ProjectTrash.TrashAsync(
            new ProjectIndex(_root), WireframeProject.At(notAProject),
            TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }
}
