using System.Text;
using Ivy.Tendril.Wireframe.Console.Assets;
using Ivy.Tendril.Wireframe.Console.Project;

namespace Ivy.Tendril.Wireframe.Console.Hosting;

/// <summary>
/// Injects everything the page needs into the user's index.html: stylesheets, the import
/// map, the bundle, and (in serve mode) the live-reload client.
///
/// The user's index.html stays editable and is never rewritten on disk -- this happens on
/// the way out of the server.
/// </summary>
public sealed class IndexHtmlBuilder(VendorManifest vendor)
{
    /// <summary>
    /// Stylesheet order is load-bearing. tendril.css declares
    /// <c>@layer properties, theme, base, utilities</c> first, which fixes that order for
    /// the whole document; the utility superset then emits into the same theme/utilities
    /// layers and must come after so it wins on equal specificity. fonts.css is last so
    /// its @font-face rules beat any that a stray Google Fonts sheet might contribute.
    /// </summary>
    private static readonly string[] Stylesheets =
    [
        "/__wireframe/css/tendril.css",
        "/__wireframe/css/wireframe-utilities.css",
        "/__wireframe/css/fonts.css",
    ];

    public string Build(WireframeProject project, bool liveReload)
    {
        var html = File.Exists(project.IndexHtml)
            ? File.ReadAllText(project.IndexHtml)
            : ScaffoldTemplates.IndexHtml.Replace("{{TITLE}}", project.Name);

        var head = new StringBuilder();
        foreach (var href in Stylesheets)
            head.Append($"    <link rel=\"stylesheet\" href=\"{href}\">\n");

        head.Append("    <script type=\"importmap\">\n");
        head.Append(vendor.ToImportMapJson());
        head.Append("\n    </script>\n");

        var body = new StringBuilder();
        body.Append("    <script type=\"module\" src=\"/__wireframe/out/bundle.js\"></script>\n");
        if (liveReload)
            body.Append("    <script src=\"/__wireframe/client.js\"></script>\n");

        html = InsertBefore(html, "</head>", head.ToString());
        html = InsertBefore(html, "</body>", body.ToString());
        return html;
    }

    /// <summary>
    /// Inserts before a closing tag, appending if the document does not have one. Keeps a
    /// hand-edited index.html working even if the user removed the tag.
    /// </summary>
    private static string InsertBefore(string html, string tag, string insert)
    {
        var index = html.LastIndexOf(tag, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? html + insert : html.Insert(index, insert);
    }
}
