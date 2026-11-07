using AngleSharp.Html.Parser;

namespace Ume.Rag.Worker.Connectors.Render;

/// <summary>
/// The rendered DOM as the document's raw content (connectors design §8.3, "Output"): <c>IPage.ContentAsync()</c>, serialised
/// by Playwright in its isolated world so page script cannot tamper with it, cleaned here in the worker: <c>script</c>,
/// <c>noscript</c>, <c>style</c>, <c>template</c> and <c>iframe</c> elements and inline <c>on*</c> handlers removed.
/// Everything downstream (links, the noise hash, the raw blob, parsing) reads the result as any fetched HTML.
/// </summary>
public static class RenderedHtml
{
    private static readonly HtmlParser Parser = new();

    public static string Clean(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var document = Parser.ParseDocument(content);
        foreach (var element in document.QuerySelectorAll("script, noscript, style, template, iframe").ToList())
        {
            element.Remove();
        }

        foreach (var element in document.All)
        {
            foreach (var attribute in element.Attributes.Where(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                element.RemoveAttribute(attribute.Name);
            }
        }

        return "<!DOCTYPE html>" + document.DocumentElement.OuterHtml;
    }
}
