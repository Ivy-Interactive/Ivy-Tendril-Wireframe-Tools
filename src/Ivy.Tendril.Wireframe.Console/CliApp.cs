using Ivy.Tendril.Wireframe.Console.Commands;
using Spectre.Console.Cli;

namespace Ivy.Tendril.Wireframe.Console;

/// <summary>
/// Builds the command tree. Kept separate from <c>Program</c> so end-to-end tests can run
/// the whole CLI in-process.
/// </summary>
public static class CliApp
{
    public static CommandApp Build()
    {
        var app = new CommandApp();

        app.Configure(config =>
        {
            config.SetApplicationName("wireframe");
            config.UseStrictParsing();
            config.CaseSensitivity(CaseSensitivity.None);

            config.AddCommand<SetupCommand>("setup")
                .WithDescription("Scaffold a wireframe project that is ready for an agent to edit.")
                .WithExample("setup", "./mock")
                .WithExample("setup", "./mock", "--tailwind", "jit");

            config.AddCommand<ServeCommand>("serve")
                .WithDescription("Serve a wireframe project on a free port with hot reload.")
                .WithExample("serve", "./mock")
                .WithExample("serve", "./mock", "--open");

            config.AddCommand<ScreenshotCommand>("screenshot")
                .WithDescription("Render a wireframe to screenshots/<width>x<height>.png.")
                .WithExample("screenshot", "./mock")
                .WithExample("screenshot", "./mock", "-w", "1440", "--height", "900");

            config.AddCommand<AgentReadmeCommand>("agent-readme")
                .WithDescription("Print instructions and the full component/prop reference for an agent.")
                .WithExample("agent-readme")
                .WithExample("agent-readme", "--component", "Button");
        });

        return app;
    }
}
