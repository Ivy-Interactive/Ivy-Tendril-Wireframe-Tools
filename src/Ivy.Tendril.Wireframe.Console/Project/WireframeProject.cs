using System.Security.Cryptography;
using System.Text;

namespace Ivy.Tendril.Wireframe.Console.Project;

/// <summary>
/// The on-disk layout of a wireframe project.
///
/// <code>
/// &lt;root&gt;/
///   index.html          committed, editable
///   tsconfig.json       committed, extends .wireframe/tsconfig.base.json
///   src/                committed -- the agent's work
///   public/             committed -- static assets
///   screenshots/        committed -- the deliverable
///   .wireframe/         GITIGNORED, regenerated on every run, never user-authored
/// </code>
///
/// Build output deliberately does NOT live under the project: it goes to a per-project
/// directory under the user's local app data, so there is no stray dist/ to gitignore and
/// `screenshot` can build in isolation without racing a running `serve`.
/// </summary>
public sealed class WireframeProject
{
    public required string Root { get; init; }

    public string SourceDir => Path.Combine(Root, "src");
    public string PublicDir => Path.Combine(Root, "public");
    public string ScreenshotsDir => Path.Combine(Root, "screenshots");
    public string WorkDir => Path.Combine(Root, ".wireframe");
    public string TypesDir => Path.Combine(WorkDir, "types");
    public string StampFile => Path.Combine(WorkDir, ".stamp");
    public string IndexHtml => Path.Combine(Root, "index.html");
    public string TsConfig => Path.Combine(Root, "tsconfig.json");
    public string EntryPoint => Path.Combine(SourceDir, "main.tsx");

    public string Name => new DirectoryInfo(Root).Name;

    public static WireframeProject At(string path) =>
        new() { Root = Path.GetFullPath(path) };

    public bool Exists => Directory.Exists(SourceDir) && File.Exists(EntryPoint);

    /// <summary>
    /// Build output directory, keyed by a hash of the project path. Separate subdirectories
    /// per purpose so a `screenshot` run can never observe a half-written `serve` bundle.
    /// </summary>
    public string OutDir(string purpose)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToLowerInvariant())))[..12].ToLowerInvariant();

        var baseDir = Environment.GetEnvironmentVariable("WIREFRAME_CACHE")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "wireframe");

        return Path.Combine(baseDir, "builds", $"{SanitizeName(Name)}-{hash}", purpose);
    }

    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "wireframe" : s;
    }

    /// <summary>Files the agent authors, in bundle order relevance. Used by the linter.</summary>
    public IEnumerable<string> SourceFiles() =>
        Directory.Exists(SourceDir)
            ? Directory.EnumerateFiles(SourceDir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            : [];

    public string RelativePath(string absolute) =>
        Path.GetRelativePath(Root, absolute).Replace('\\', '/');
}
