using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ivy.Tendril.Wireframe.Studio.Projects;

/// <summary>
/// Opens a file or folder in VS Code.
///
/// Resolution order: WIREFRAME_EDITOR, then the `code` CLI on PATH, then the well-known
/// install locations -- VS Code's installer offers the PATH shim as an opt-in, so plenty
/// of working installs are not on PATH.
/// </summary>
public static class EditorLauncher
{
    public sealed record Result(bool Ok, string? Error);

    /// <summary>True when an editor could be found, so the UI can disable the button.</summary>
    public static bool IsAvailable => Locate() is not null;

    public static Result Open(string path, int line = 0)
    {
        var editor = Locate();
        if (editor is null)
        {
            return new Result(false,
                "VS Code was not found. Install the `code` command (in VS Code: " +
                "Command Palette → \"Shell Command: Install 'code' command in PATH\"), " +
                "or set WIREFRAME_EDITOR to its executable.");
        }

        try
        {
            var psi = new ProcessStartInfo(editor)
            {
                // Without this, `code.cmd` on Windows flashes a console window.
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (line > 0)
            {
                // --goto needs the file and line as one argument.
                psi.ArgumentList.Add("--goto");
                psi.ArgumentList.Add($"{path}:{line}");
            }
            else
            {
                psi.ArgumentList.Add(path);
            }

            Process.Start(psi);
            return new Result(true, null);
        }
        catch (Exception e)
        {
            return new Result(false, $"Could not start VS Code: {e.Message}");
        }
    }

    private static string? Locate()
    {
        var overridePath = Environment.GetEnvironmentVariable("WIREFRAME_EDITOR");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "code.cmd", "code.exe", "code-insiders.cmd" }
            : ["code", "code-insiders"];

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var name in names)
                {
                    string candidate;
                    try { candidate = Path.Combine(dir, name); }
                    catch (ArgumentException) { continue; }
                    yield return candidate;
                }
            }
        }

        // The PATH shim is optional in VS Code's installer, so fall back to where it lands.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programs = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            yield return Path.Combine(local, @"Programs\Microsoft VS Code\bin\code.cmd");
            yield return Path.Combine(programs, @"Microsoft VS Code\bin\code.cmd");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code";
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Applications/Visual Studio Code.app/Contents/Resources/app/bin/code");
        }
        else
        {
            yield return "/usr/bin/code";
            yield return "/usr/local/bin/code";
            yield return "/snap/bin/code";
        }
    }
}
