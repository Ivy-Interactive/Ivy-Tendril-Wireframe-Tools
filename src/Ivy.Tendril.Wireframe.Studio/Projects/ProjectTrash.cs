using Ivy.Tendril.Wireframe.Console.Project;

namespace Ivy.Tendril.Wireframe.Studio.Projects;

/// <summary>
/// Removes a wireframe project by moving it into <c>&lt;root&gt;/.trash/</c> rather than
/// deleting it.
///
/// A recursive directory delete from a button in a browser is irreversible, and a
/// wireframe is someone's work. Moving it is just as effective from the UI's point of
/// view -- the rail skips dot-directories, so it disappears from the list -- while staying
/// recoverable with a single `mv`. Purging .trash is then a deliberate act outside Studio.
/// </summary>
public static class ProjectTrash
{
    public const string TrashDirectoryName = ".trash";

    public sealed record Result(bool Ok, string? Destination, string? Error);

    public static Result Trash(ProjectIndex index, WireframeProject project)
    {
        var root = Path.GetFullPath(index.Root);
        var target = Path.GetFullPath(project.Root);

        // Refuse to trash the directory being scanned. It is almost always a mistake, and
        // it would leave Studio pointing at nothing.
        if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
        {
            return new Result(false, null,
                "This is the directory Studio is scanning, not a project inside it. " +
                "Remove it from outside Studio if that is really what you want.");
        }

        // Must live under the scanned root: the name comes off an HTTP route.
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new Result(false, null, "That project is not inside the scanned directory.");
        }

        // And must actually look like a wireframe, so a mis-resolved name cannot take out
        // an unrelated folder.
        if (!project.Exists)
        {
            return new Result(false, null, "That directory is not a wireframe project.");
        }

        try
        {
            var trashRoot = Path.Combine(root, TrashDirectoryName);
            Directory.CreateDirectory(trashRoot);

            // Timestamped so trashing the same name twice does not collide.
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var destination = Path.Combine(trashRoot, $"{project.Name}-{stamp}");

            var attempt = 1;
            while (Directory.Exists(destination))
                destination = Path.Combine(trashRoot, $"{project.Name}-{stamp}-{++attempt}");

            Directory.Move(target, destination);
            return new Result(true, destination, null);
        }
        catch (IOException e)
        {
            // The usual cause is a file still open -- an editor, or a build watcher that
            // was not torn down before the move.
            return new Result(false, null,
                $"Could not move the project: {e.Message} " +
                "Something may still have a file open inside it.");
        }
        catch (UnauthorizedAccessException e)
        {
            return new Result(false, null, $"Access denied: {e.Message}");
        }
    }
}
