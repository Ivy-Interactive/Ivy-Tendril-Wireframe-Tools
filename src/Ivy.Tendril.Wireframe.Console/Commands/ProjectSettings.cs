using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ivy.Tendril.Wireframe.Console.Commands;

/// <summary>Shared base for the commands that operate on a project directory.</summary>
public class ProjectSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Project directory. Defaults to the current directory.")]
    public string Path { get; init; } = ".";

    [CommandOption("--quiet")]
    [Description("Print only errors.")]
    public bool Quiet { get; init; }
}
