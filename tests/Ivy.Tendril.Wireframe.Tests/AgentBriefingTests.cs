using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;
using Ivy.Tendril.Wireframe.Studio.Terminal;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// The agent starts with no idea what Tendril is, so the briefing is what makes the
/// terminal useful rather than just present. This regressed once already: swapping the
/// chat panel for a PTY dropped the system prompt and the reference write with it.
/// </summary>
public class AgentBriefingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wireframe-brief-tests", Guid.NewGuid().ToString("N")[..8]);

    private WireframeProject Scaffold()
    {
        var project = WireframeProject.At(_root);
        new ProjectScaffolder(AssetCatalog.Default).Scaffold(project);
        return project;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Reference_is_written_into_the_project()
    {
        var project = Scaffold();
        AgentBriefing.WriteReference(project);

        var path = Path.Combine(project.Root, AgentBriefing.ReferenceFileName);
        Assert.True(File.Exists(path));

        var text = File.ReadAllText(path);
        Assert.Contains("**Button**", text);
        Assert.Contains("SketchProvider", text);
        // The whole point is that it is the full component reference, not a summary.
        Assert.InRange(text.Length, 20_000, 80_000);
    }

    [Fact]
    public void Reference_is_refreshed_rather_than_left_stale()
    {
        // A tool upgrade regenerates the components; a reference written once at setup
        // would keep describing the old ones.
        var project = Scaffold();
        var path = Path.Combine(project.Root, AgentBriefing.ReferenceFileName);
        File.WriteAllText(path, "stale");

        AgentBriefing.WriteReference(project);

        Assert.DoesNotContain("stale", File.ReadAllText(path));
    }

    [Fact]
    public void System_prompt_states_the_purpose_and_points_at_the_reference()
    {
        var prompt = AgentBriefing.SystemPrompt(Scaffold());

        Assert.Contains("wireframe", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AgentBriefing.ReferenceFileName, prompt);
        Assert.Contains("wireframe agent-readme", prompt);
        Assert.Contains(_root, prompt);
    }

    [Fact]
    public void System_prompt_carries_the_rules_that_are_easy_to_get_wrong()
    {
        var prompt = AgentBriefing.SystemPrompt(Scaffold());

        Assert.Contains("SketchProvider", prompt);
        Assert.Contains("signalWireframeReady", prompt);
        Assert.Contains("w-[347px]", prompt);          // arbitrary values are not generated
        Assert.Contains("wireframe screenshot", prompt);
        Assert.Contains("wireframe serve", prompt);    // and that it must not be run
    }

    [Fact]
    public void System_prompt_stays_short_enough_to_prepend_every_turn()
    {
        // The long reference is a file the agent reads on demand. If this ever approaches
        // the size of AGENT.md, the split has broken down.
        var prompt = AgentBriefing.SystemPrompt(Scaffold());
        Assert.InRange(prompt.Length, 500, 4000);
    }
}
