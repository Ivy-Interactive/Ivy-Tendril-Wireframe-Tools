using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Studio.Agent;
using Ivy.Tendril.Wireframe.Studio.Hosting;
using Ivy.Tendril.Wireframe.Studio.Projects;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ivy.Tendril.Wireframe.Studio;

public sealed class StudioSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Directory to scan for wireframe projects. Defaults to the current directory.")]
    public string Path { get; init; } = ".";

    [CommandOption("--port <PORT>")]
    [Description("Bind a specific port instead of a free one.")]
    public int Port { get; init; }

    [CommandOption("--no-open")]
    [Description("Do not open a browser on start.")]
    public bool NoOpen { get; init; }

    [CommandOption("--print-url")]
    [Description("Print only the URL, for scripting.")]
    public bool PrintUrl { get; init; }
}

public sealed class StudioCommand : AsyncCommand<StudioSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, StudioSettings settings, CancellationToken cancellation)
    {
        var root = System.IO.Path.GetFullPath(settings.Path);
        if (!Directory.Exists(root))
        {
            AnsiConsole.MarkupLine($"[red]No such directory:[/] [blue]{root.EscapeMarkup()}[/]");
            return 1;
        }

        var index = new ProjectIndex(root);
        var projects = index.List();

        await using var server = new StudioServer(AssetCatalog.Default, index, settings.Port);
        await server.StartAsync(cancellation);

        if (settings.PrintUrl)
        {
            System.Console.Out.WriteLine(server.Url);
        }
        else
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  [green]wireframe studio[/]  [link={server.Url}]{server.Url}[/]");
            AnsiConsole.MarkupLine($"  [grey]scanning   {root.EscapeMarkup()}[/]");
            AnsiConsole.MarkupLine(projects.Count switch
            {
                0 => "  [yellow]![/] [grey]no wireframe projects found yet; create one with " +
                     "[/][blue]wireframe setup <path>[/]",
                1 => "  [grey]found      1 project[/]",
                _ => $"  [grey]found      {projects.Count} projects[/]",
            });

            if (!AgentSession.IsAvailable)
            {
                AnsiConsole.MarkupLine(
                    "  [yellow]![/] [grey]the `claude` CLI is not on PATH, so the agent panel will be " +
                    "inert. Everything else works.[/]");
            }

            AnsiConsole.MarkupLine("  [grey]ctrl+c to stop[/]");
            AnsiConsole.WriteLine();
        }

        if (!settings.NoOpen && !settings.PrintUrl) TryOpenBrowser(server.Url);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellation);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C; disposal stops the preview server and any agent process.
        }

        if (!settings.PrintUrl)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("  [grey]stopped[/]");
        }
        return 0;
    }

    /// <summary>Opening a browser must never take the server down with it.</summary>
    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                var browser = Environment.GetEnvironmentVariable("BROWSER");
                var candidates = string.IsNullOrWhiteSpace(browser)
                    ? new[] { "xdg-open", "gio", "sensible-browser", "x-www-browser" }
                    : [browser];

                foreach (var exe in candidates)
                {
                    try
                    {
                        Process.Start(exe, exe == "gio" ? $"open {url}" : url);
                        return;
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
                    {
                        // Next launcher.
                    }
                }
            }
        }
        catch (Exception e)
        {
            AnsiConsole.MarkupLine($"  [yellow]![/] [grey]could not open a browser ({e.Message.EscapeMarkup()})[/]");
        }
    }
}
