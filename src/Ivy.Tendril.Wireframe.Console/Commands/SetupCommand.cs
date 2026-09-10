using System.ComponentModel;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ivy.Tendril.Wireframe.Console.Commands;

public sealed class SetupSettings : ProjectSettings
{
    [CommandOption("--tailwind <MODE>")]
    [Description("'superset' (default, embedded, offline) or 'jit' (downloads the ~107 MB Tailwind standalone CLI for full fidelity).")]
    public string Tailwind { get; init; } = "superset";

    [CommandOption("--force")]
    [Description("Rewrite scaffold files that already exist.")]
    public bool Force { get; init; }
}

public sealed class SetupCommand : Command<SetupSettings>
{
    protected override int Execute(CommandContext context, SetupSettings settings, CancellationToken cancellation)
    {
        var assets = AssetCatalog.Default;
        var project = WireframeProject.At(settings.Path);

        if (settings.Force && Directory.Exists(project.SourceDir))
        {
            foreach (var f in new[] { "main.tsx", "App.tsx", "wireframe-ready.ts" })
            {
                var p = System.IO.Path.Combine(project.SourceDir, f);
                if (File.Exists(p)) File.Delete(p);
            }
            if (File.Exists(project.IndexHtml)) File.Delete(project.IndexHtml);
            if (File.Exists(project.TsConfig)) File.Delete(project.TsConfig);
        }

        var scaffolder = new ProjectScaffolder(assets);
        var result = scaffolder.Scaffold(project);
        var manifest = VendorManifest.Load(assets);

        if (settings.Quiet) return 0;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [green]wireframe[/] project ready at [blue]{project.Root.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();

        foreach (var f in result.Created)
            AnsiConsole.MarkupLine($"    [green]+[/] {f.EscapeMarkup()}");
        foreach (var f in result.Skipped)
            AnsiConsole.MarkupLine($"    [grey]. {f.EscapeMarkup()} (exists, kept)[/]");

        AnsiConsole.MarkupLine($"    [grey]. .wireframe/ ({result.TypeFiles} type files)[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"  [grey]tendril-wireframes {manifest.TendrilVersion}  ·  react {manifest.ReactVersion}  ·  no node required[/]");

        if (!string.Equals(settings.Tailwind, "superset", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("  [yellow]![/] [grey]--tailwind jit is not wired up yet; using the embedded superset.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  next:");
        AnsiConsole.MarkupLine($"    [blue]wireframe serve {Rel(project.Root)}[/]        live preview with hot reload");
        AnsiConsole.MarkupLine($"    [blue]wireframe screenshot {Rel(project.Root)}[/]   render to screenshots/");
        AnsiConsole.MarkupLine("    [blue]wireframe agent-readme[/]          component and prop reference");
        AnsiConsole.WriteLine();

        return 0;
    }

    private static string Rel(string root)
    {
        var rel = System.IO.Path.GetRelativePath(Directory.GetCurrentDirectory(), root);
        return rel == "." ? "." : rel.Replace('\\', '/');
    }
}
