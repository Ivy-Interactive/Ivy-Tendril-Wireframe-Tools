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

        Assert.All(
            manifest.Types.Values.Where(t => t.Values is not null),
            type => Assert.All(type.Values!, v => Assert.NotNull(v.Value)));
    }

    /// <summary>
    /// `Sizing` is named by 200 props and used to be defined nowhere, so an agent reading
    /// `width: Sizing` guessed pixels -- and `width={150}` silently drew a 600px box.
    /// </summary>
    [Fact]
    public void Every_type_a_prop_names_is_described()
    {
        var manifest = ComponentManifest.Load(Assets);

        var sizing = Assert.Contains("Sizing", manifest.Types);
        Assert.Equal("alias", sizing.Kind);
        Assert.Equal("number | string", sizing.Type);
        Assert.NotNull(sizing.Description);

        // The description has to carry the trap itself, not just the type expression.
        Assert.Contains("4px", sizing.Description);

        var thickness = Assert.Contains("Thickness", manifest.Types);
        Assert.Equal("object", thickness.Kind);
        Assert.Contains(thickness.Properties!, p => p.Name == "left" && p.Type == "Sizing");

        var chartData = Assert.Contains("ChartData", manifest.Types);
        Assert.Equal("map", chartData.Kind);

        var variant = Assert.Contains("ButtonVariant", manifest.Types);
        Assert.Equal("enum", variant.Kind);
        Assert.Contains(variant.Values!, v => v.Value == "Primary");
    }

    [Fact]
    public void The_reference_explains_the_types_the_props_use()
    {
        var text = Renderer().Render();

        Assert.Contains("### Shapes and aliases", text);
        Assert.Contains("**`Sizing`** = number | string", text);
        Assert.Contains("4px", text);
        Assert.Contains("`left?: Sizing`", text);
    }
}
