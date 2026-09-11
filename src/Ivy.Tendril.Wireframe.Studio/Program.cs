using System.Text;
using Ivy.Tendril.Wireframe.Studio;
using Spectre.Console.Cli;

System.Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// A single-command app: `wireframe-studio [path] [options]`.
var app = new CommandApp<StudioCommand>();
app.Configure(config =>
{
    config.SetApplicationName("wireframe-studio");
    config.UseStrictParsing();
    config.AddExample(".");
    config.AddExample("./wireframes", "--port", "7600");
});

return app.Run(args);
