using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Manifest;
using Ivy.Tendril.Wireframe.Console.Project;

namespace Ivy.Tendril.Wireframe.Studio.Terminal;

/// <summary>
/// Tells the agent what it is working on before it sees a prompt.
///
/// Two pieces, deliberately split by size. The system prompt is short and always present:
/// what this session is for, the handful of rules that are easy to get wrong, and where
/// the full reference lives. AGENT.md is the ~40 KB component reference, written into the
/// project so the agent reads it once on demand rather than carrying it in every turn.
/// </summary>
public static class AgentBriefing
{
    public const string ReferenceFileName = "AGENT.md";

    /// <summary>
    /// Writes the component reference into the project.
    ///
    /// Rewritten on every session rather than once at setup: it is generated from the
    /// payload embedded in this build, so a tool upgrade would otherwise leave a stale
    /// reference describing components that have changed.
    /// </summary>
    public static void WriteReference(WireframeProject project)
    {
        try
        {
            var assets = AssetCatalog.Default;
            var renderer = new AgentReadmeRenderer(
                ComponentManifest.Load(assets), VendorManifest.Load(assets));

            File.WriteAllText(
                Path.Combine(project.Root, ReferenceFileName), renderer.Render());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The session still works: the system prompt tells the agent to run
            // `wireframe agent-readme` if the file is not there.
        }
    }

    /// <summary>
    /// Appended to Claude Code's own system prompt. Kept short on purpose -- the long
    /// reference is a file it can read, not something to repeat on every turn.
    /// </summary>
    public static string SystemPrompt(WireframeProject project) =>
        string.Join('\n',
        [
            "# Wireframe Studio session",
            "",
            "You are editing a hand-drawn wireframe mockup, live. The user is watching a",
            "preview of it beside this terminal that reloads as you save, alongside the",
            "source and a screenshot gallery.",
            "",
            $"Project: {project.Root}",
            "",
            "## Read this first",
            "",
            $"`./{ReferenceFileName}` in this project is the complete reference for the CLI and",
            "the `tendril-wireframes` component library: all 98 components with their props,",
            "the conventions, and what Tailwind support exists. Read it before your first",
            "edit. If it is missing, run `wireframe agent-readme`.",
            "",
            "## Rules that are easy to get wrong",
            "",
            "- The whole app is under `src/`. Edit `src/App.tsx`; add files there if it helps.",
            "- Keep `SketchProvider`, the `tendril` class and `signalWireframeReady()` in",
            "  `src/main.tsx`. Without them the components render, but not as a wireframe,",
            "  and screenshots may catch a half-drawn frame.",
            "- Only these imports resolve: `tendril-wireframes`, `react`, `react-dom/client`,",
            "  `lucide-react`. There is no package manager here -- do not add dependencies",
            "  or write a package.json.",
            "- Props are named enums in PascalCase: `variant=\"Destructive\"`, `density=\"Small\"`.",
            "  Lowercase will not match.",
            "- Arbitrary Tailwind values (`w-[347px]`) are not generated. Use `style={{ }}` or",
            "  a component's own size prop. Classes that produce no CSS are reported to you.",
            "- Structural layout is plain flex/grid; anything that should look drawn is a",
            "  Tendril component.",
            "",
            "## Checking your work",
            "",
            "The Studio already shows the user a live preview, so you do not need to run a",
            "server. To see the result yourself:",
            "",
            "    wireframe screenshot .",
            "",
            "then read the PNG it writes under `screenshots/`. Look at it and iterate -- a",
            "wireframe that occupies the top third of the viewport is not finished.",
            "",
            "Never run `wireframe serve`: it blocks forever and the preview is already running.",
        ]);
}
