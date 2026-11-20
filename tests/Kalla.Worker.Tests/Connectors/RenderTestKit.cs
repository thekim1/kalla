using System.Collections.Concurrent;
using Kalla.Worker.Connectors.Render;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// An <see cref="IRenderer"/> without a browser: <see cref="Render"/> decides each page from the request (the main document
/// the worker fetched) and may call the page's <see cref="IRenderFetcher"/> as a browser would; without it every render is
/// <c>render.unavailable</c>. <see cref="Requests"/> records what the worker asked for.
/// </summary>
internal sealed class FakeRenderer : IRenderer
{
    public Func<RenderRequest, IRenderFetcher, Task<RenderResult>>? Render { get; set; }

    public ConcurrentQueue<RenderRequest> Requests { get; } = new();

    /// <summary>The main documents as the browser got them, by path (the body is gone once the fetch has stored the page).</summary>
    public ConcurrentDictionary<string, string> MainDocuments { get; } = new(StringComparer.Ordinal);

    public static RenderStats NoRequests { get; } = new(0, 0, 0, 0, 0, 0, TimeSpan.FromMilliseconds(5));

    /// <summary>A rendered page: <paramref name="html"/> ready, with <paramref name="discovered"/> links.</summary>
    public static RenderResult Page(string html, params Uri[] discovered) => new(html, NoRequests, Ready: true) { Discovered = discovered };

    /// <summary>A page that was not ready within the page timeout, for <paramref name="reason"/> (<see cref="RenderResult.NotReadyReason"/>).</summary>
    public static RenderResult NotReady(string reason = RenderReasons.Timeout) => new(string.Empty, NoRequests, Ready: false) { NotReadyReason = reason };

    /// <summary>A page that navigated to <paramref name="target"/> before it was ready.</summary>
    public static RenderResult Navigated(Uri target) => new(string.Empty, NoRequests, Ready: false) { Navigation = target };

    public async ValueTask<RenderResult> RenderAsync(RenderRequest request, IRenderFetcher fetcher, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        await using (var stream = request.MainDocument.OpenRead())
        using (var reader = new StreamReader(stream))
        {
            MainDocuments[request.Url.AbsolutePath] = await reader.ReadToEndAsync(cancellationToken);
        }

        return Render is { } render ? await render(request, fetcher) : throw new RenderUnavailableException();
    }
}

internal static class RenderTestKit
{
    /// <summary>A configured renderer (the endpoint is never dialled by a <see cref="FakeRenderer"/>).</summary>
    public static RenderOptions Options(int maxConcurrent = 2) =>
        new() { Endpoint = "ws://renderer.test:3000/render-test-path-0123456789", MaxConcurrent = maxConcurrent };
}
