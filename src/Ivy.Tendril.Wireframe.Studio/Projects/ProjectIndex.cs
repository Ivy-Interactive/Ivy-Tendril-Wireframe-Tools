using System.Text.Json.Serialization;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;

namespace Ivy.Tendril.Wireframe.Studio.Projects;

public sealed record ProjectSummary(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("fileCount")] int FileCount,
    [property: JsonPropertyName("screenshotCount")] int ScreenshotCount,
    [property: JsonPropertyName("suggestionCount")] int SuggestionCount,
    [property: JsonPropertyName("modified")] DateTimeOffset Modified);

public sealed record SourceFileInfo(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("bytes")] long Bytes);

public sealed record SuggestionInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    /// <summary>Absolute, so it can be pasted straight into an editor or an issue.</summary>
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("modified")] DateTimeOffset Modified);

public sealed record ShotInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("modified")] DateTimeOffset Modified);

/// <summary>
/// Finds the wireframe projects under a root directory and reads their contents.
///
/// A directory is a wireframe project if it has src/main.tsx -- the same test `serve` and
/// `screenshot` use, so Studio never lists something those commands would reject. The root
/// itself counts, so pointing Studio at a single project works as well as pointing it at a
/// folder of them.
/// </summary>
public sealed class ProjectIndex(string root)
{
    /// <summary>How deep to look. Two levels covers root/, root/*/ and root/*/*/ without
    /// walking an entire source tree if someone points Studio at a repo.</summary>
    private const int MaxDepth = 2;

    public string Root { get; } = System.IO.Path.GetFullPath(root);

    public IReadOnlyList<ProjectSummary> List()
    {
        var found = new List<ProjectSummary>();

        foreach (var directory in Candidates(Root, 0))
        {
            var project = WireframeProject.At(directory);
            if (!project.Exists) continue;

            var files = SourceFiles(project).ToList();
            var shots = Shots(project).ToList();

            var modified = files
                .Select(f => File.GetLastWriteTimeUtc(System.IO.Path.Combine(project.Root, f.Path)))
                .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(project.Root))
                .Max();

            found.Add(new ProjectSummary(
                project.Name, project.Root, files.Count, shots.Count, Suggestions(project).Count,
                new DateTimeOffset(modified, TimeSpan.Zero)));
        }

        return found.OrderByDescending(p => p.Modified).ToList();
    }

    private static IEnumerable<string> Candidates(string directory, int depth)
    {
        yield return directory;
        if (depth >= MaxDepth) yield break;

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            var name = System.IO.Path.GetFileName(child);
            // Skip the directories that are never a wireframe and are expensive to walk.
            if (name is "node_modules" or "bin" or "obj" or ".git" or ".wireframe"
                or "screenshots" or "suggestions")
                continue;
            if (name.StartsWith('.')) continue;

            foreach (var result in Candidates(child, depth + 1)) yield return result;
        }
    }

    /// <summary>Resolves a project by name. Returns null rather than throwing so the API can
    /// answer 404 for a project that was deleted while the browser had it selected.</summary>
    public WireframeProject? Find(string name)
    {
        var match = List().FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : WireframeProject.At(match.Path);
    }

    public static IEnumerable<SourceFileInfo> SourceFiles(WireframeProject project) =>
        project.SourceFiles()
            .Select(f => new SourceFileInfo(project.RelativePath(f), new FileInfo(f).Length))
            .OrderBy(f => f.Path.EndsWith("App.tsx") ? 0 : 1)
            .ThenBy(f => f.Path, StringComparer.Ordinal);

    public static IEnumerable<ShotInfo> Shots(WireframeProject project)
    {
        if (!Directory.Exists(project.ScreenshotsDir)) return [];

        return Directory.EnumerateFiles(project.ScreenshotsDir, "*.png")
            .Select(file =>
            {
                var info = new FileInfo(file);
                var (width, height) = ParseSize(info.Name);
                return new ShotInfo(
                    info.Name, width, height, info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            })
            .OrderByDescending(s => s.Modified)
            .ToList();
    }

    /// <summary>
    /// The agent's notes, newest first. Only `.html`: the folder is the agent's to write
    /// into, and anything else it leaves there is not something Studio can render.
    /// </summary>
    public static IReadOnlyList<SuggestionInfo> Suggestions(WireframeProject project)
    {
        if (!Directory.Exists(project.SuggestionsDir)) return [];

        return Directory.EnumerateFiles(project.SuggestionsDir, "*.html")
            .Select(file =>
            {
                var info = new FileInfo(file);
                return new SuggestionInfo(
                    info.Name, ReadTitle(file, info.Name), info.FullName, info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            })
            .OrderByDescending(s => s.Modified)
            .ToList();
    }

    /// <summary>
    /// Pulls &lt;title&gt; out for the list, falling back to the filename. Only the head of
    /// the file is read: these are whole pages, and the list must stay cheap enough to
    /// rebuild on every watcher event.
    /// </summary>
    private static string ReadTitle(string file, string fallback)
    {
        try
        {
            var buffer = new char[4096];
            using var reader = new StreamReader(file);
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var head = new string(buffer, 0, read);

            var open = head.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
            if (open < 0) return fallback;
            var start = head.IndexOf('>', open);
            var close = start < 0 ? -1 : head.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return fallback;

            var title = System.Net.WebUtility.HtmlDecode(head[(start + 1)..close]).Trim();
            return title.Length == 0 ? fallback : title;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Reads the CSS dimensions back out of the filename `screenshot` writes
    /// (&lt;width&gt;x&lt;height&gt;.png, or &lt;width&gt;xfull.png). Used only for the
    /// gallery's aspect ratio, so an unparseable name falls back to 4:3 rather than failing.
    /// </summary>
    private static (int Width, int Height) ParseSize(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var at = stem.IndexOf('@');
        if (at > 0) stem = stem[..at];

        var parts = stem.Split('x', 2);
        if (parts.Length == 2 && int.TryParse(parts[0], out var width))
        {
            if (int.TryParse(parts[1], out var height)) return (width, height);
            // "1440xfull": tall shots are clamped for display; the real height is unknown
            // until the image loads, and object-cover handles the overflow.
            if (parts[1].Equals("full", StringComparison.OrdinalIgnoreCase)) return (width, width * 2);
        }
        return (1440, 900);
    }

    /// <summary>
    /// Turns a user-typed name into a directory name, or returns null when it cannot be
    /// made safe. The name arrives from a browser field, so it must be a single segment
    /// that stays inside the scanned root.
    /// </summary>
    public static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var trimmed = name.Trim();
        if (trimmed is "." or "..") return null;
        if (trimmed.StartsWith('.')) return null;          // would hide from the rail
        if (trimmed.Length > 64) return null;

        // No separators: a name is one directory, never a path. GetInvalidFileNameChars
        // covers both slashes on Windows but not on Unix, so they are checked explicitly.
        if (trimmed.Contains('/') || trimmed.Contains('\\')) return null;
        if (trimmed.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;

        return trimmed;
    }

    /// <summary>Creates a new wireframe project under the scanned root.</summary>
    public ProjectSummary? Create(string name, AssetCatalog assets, out string? error)
    {
        var safe = SanitizeName(name);
        if (safe is null)
        {
            error = "Use a short name without slashes, and not starting with a dot.";
            return null;
        }

        var target = System.IO.Path.Combine(Root, safe);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            error = $"'{safe}' already exists.";
            return null;
        }

        new ProjectScaffolder(assets).Scaffold(WireframeProject.At(target));
        error = null;

        return List().FirstOrDefault(p =>
            string.Equals(p.Name, safe, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a path inside a project's src/, refusing anything that escapes it.</summary>
    public static string? ResolveSourcePath(WireframeProject project, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;

        var full = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(project.Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        // Editing is confined to src/: the agent and the editor have no business writing
        // to .wireframe/, screenshots/ or anywhere above the project.
        var sourceRoot = System.IO.Path.GetFullPath(project.SourceDir) + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
