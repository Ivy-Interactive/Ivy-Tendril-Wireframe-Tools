using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Manifest;

namespace Ivy.Tendril.Wireframe.Tests;

/// <summary>
/// The agent-readme is the tool's real interface: an agent's whole understanding of the
/// component library comes from it. These check that it stays complete AND small enough
/// to sit in a context window.
/// </summary>
public class AgentReadmeTests
{
    private static readonly AssetCatalog Assets = AssetCatalog.Default;

    private static AgentReadmeRenderer Renderer() =>
        new(ComponentManifest.Load(Assets), VendorManifest.Load(Assets));

    [Fact]
    public void Lists_every_public_component()
    {
        var manifest = ComponentManifest.Load(Assets);
        var readme = Renderer().Render();

        foreach (var component in manifest.PublicComponents)
            Assert.Contains($"**{component.Name}**", readme);
    }

    [Fact]
    public void Stays_small_enough_to_hand_to_an_agent()
    {
        // The raw manifest is ~300 KB. If this ever approaches that, the compression has
        // regressed and the document has stopped being usable as a prompt.
        var readme = Renderer().Render();
        Assert.InRange(readme.Length, 20_000, 80_000);
    }

    [Fact]
    public void Does_not_leak_the_React_DOM_attribute_surface()
    {
        // Components that spread HTML attributes inherit several hundred aria-*/on*/SVG
        // props. Documenting those buries the 98 components that matter.
        var readme = Renderer().Render();
        Assert.DoesNotContain("aria-labelledby", readme);
        Assert.DoesNotContain("aria-valuetext", readme);
        Assert.DoesNotContain("onPointerEnterCapture", readme);
    }

    [Fact]
    public void Covers_the_rules_an_agent_gets_wrong()
    {
        var readme = Renderer().Render();

        Assert.Contains("SketchProvider", readme);
        Assert.Contains("signalWireframeReady", readme);
        Assert.Contains("w-[347px]", readme);       // the arbitrary-value caveat
        Assert.Contains("wireframe screenshot", readme);
        Assert.Contains("no `node_modules`", readme);
    }

    [Fact]
    public void Renders_enum_values_so_PascalCase_props_can_be_copied_exactly()
    {
        var readme = Renderer().Render();
        Assert.Contains("ButtonVariant", readme);
        Assert.Contains("Destructive", readme);
    }

    [Fact]
    public void Component_detail_includes_inherited_props_and_examples()
    {
        var manifest = ComponentManifest.Load(Assets);
        var button = manifest.Find("Button");
        Assert.NotNull(button);

        var detail = Renderer().RenderComponent(button!);
        Assert.Contains("# Button", detail);
        Assert.Contains("variant", detail);
        Assert.Contains("density", detail);          // inherited, shown in the detail view
        Assert.Contains("<Button title=\"Save\"", detail);
    }

    [Theory]
    [InlineData("Button")]
    [InlineData("button")]
    [InlineData("DataTable")]
    public void Component_lookup_is_case_insensitive(string name) =>
        Assert.NotNull(ComponentManifest.Load(Assets).Find(name));

    [Fact]
    public void Manifest_scalars_that_are_not_strings_still_parse()
    {
        // WeekDay is [0,1,2,...] and some defaults are numbers or booleans; a naive
        // string-typed model throws on load.
        var manifest = ComponentManifest.Load(Assets);
        Assert.NotEmpty(manifest.Types);
        Assert.All(manifest.Types.Values, values => Assert.All(values, v => Assert.NotNull(v.Value)));
    }
}
