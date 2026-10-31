using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Processing.Parse;

/// <summary>
/// <c>detect</c>'s <see cref="IDocumentInspector"/> over <see cref="ParserClient.InspectAsync"/> (parser design §6.2):
/// the page signals the plan reads, the encryption and the refusal. Errors stay <see cref="ParserException"/>s, which
/// <c>detect</c> maps as engine design §7.3.
/// </summary>
public sealed class ParserInspector(ParserClient client) : IDocumentInspector
{
    public async Task<InspectFacts> InspectAsync(ReadOnlyMemory<byte> file, string mediaType, string fileExtension, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(file.ToArray(), writable: false);
        var result = await client.InspectAsync(stream, new ParserInspectOptions { FileExt = fileExtension, MediaType = mediaType }, cancellationToken);
        return new InspectFacts(result.Format, result.PageCount, result.Rangeable, [.. result.Pages.Select(p => new InspectPageSignals(p.Page)
        {
            TextChars = p.TextChars,
            BadChars = p.BadChars,
            GarbledScore = p.GarbledScore,
            ImageCoverage = p.ImageCoverage,
            PathObjects = p.PathObjects,
        })])
        {
            Encrypted = result.Encrypted,
            Refusal = result.Refusal,
        };
    }
}

/// <summary>
/// <c>personal-data</c>'s <see cref="INerClient"/> over <see cref="ParserClient.NerAsync"/> (parser design §7.8):
/// readiness from <c>/v1/info</c> <c>capacity.ner_ready</c> (an unreachable parser is not ready), names with code-point
/// spans, label <c>person</c> only (mixed labels are not asked for in P1), and the model as <c>name@revision</c>.
/// </summary>
public sealed class ParserNerClient(ParserClient client) : INerClient
{
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await client.GetInfoAsync(cancellationToken)).Capacity.NerReady;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<NerAnswer> RecognizeAsync(IReadOnlyList<NerText> texts, double minScore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var request = new ParserNerRequest("ner-" + Guid.CreateVersion7().ToString("N"), [.. texts.Select(t => new ParserNerText(t.Id, t.Text))])
        {
            MinScore = minScore,
        };
        var result = await client.NerAsync(request, cancellationToken);
        var name = result.Model.Repository[(result.Model.Repository.LastIndexOf('/') + 1)..];
        var revision = result.Model.Revision.Length > 7 ? result.Model.Revision[..7] : result.Model.Revision;
        return new NerAnswer([.. result.Entities.Select(e => new NerEntity(e.TextId, e.Start, e.End, e.Label, e.Score))], $"{name}@{revision}");
    }
}
