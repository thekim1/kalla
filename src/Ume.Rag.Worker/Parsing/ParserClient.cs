using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Ume.Rag.Worker.Parsing;

/// <summary>
/// The worker's client of the parser sidecar (plans/parser/design.md §7). <see cref="ConvertAsync"/> sends one document
/// or page range as <c>multipart/form-data</c> (<c>file</c> + <c>options</c>) and reads the <c>multipart/mixed</c>
/// answer: <c>result.json</c>, then <c>docling.json.gz</c> when Docling ran. A non-200 answer is thrown as
/// <see cref="ParserException"/> with the contract's error body. The caller owns the <see cref="HttpClient"/>: its base
/// address is the parser, its timeout the request deadline plus 15 s (design §10.4).
/// </summary>
public sealed class ParserClient(HttpClient http, JsonSerializerOptions? json = null)
{
    /// <summary>Largest part the client reads: the 60 MB Docling part; result.json is capped lower by the parser.</summary>
    public const long MaxPartBytes = 60L * 1024 * 1024;

    private static readonly Uri ConvertPath = new("v1/convert", UriKind.Relative);
    private static readonly Uri InfoPath = new("v1/info", UriKind.Relative);

    private readonly JsonSerializerOptions _json = json ?? ParserJson.Create();

    /// <summary>
    /// Converts <paramref name="file"/>. The Docling JSON part, when present, is copied to
    /// <paramref name="doclingGzip"/> as sent (gzip), or skipped when it is null.
    /// </summary>
    public async Task<ParserConvertResult> ConvertAsync(
        Stream file,
        ParserConvertOptions options,
        Stream? doclingGzip,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // The parser gets the extension in the options only, never a file name (design §7.1).
        content.Add(fileContent, "file", "input");
        content.Add(new StringContent(JsonSerializer.Serialize(options, _json), Encoding.UTF8, "application/json"), "options");

        using var request = new HttpRequestMessage(HttpMethod.Post, ConvertPath) { Content = content };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await ToExceptionAsync(response, cancellationToken);
        }

        var boundary = response.Content.Headers.ContentType?.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"')
            ?? throw new InvalidDataException("The parser answered without a multipart boundary.");
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var reader = new MultipartReader(boundary, body) { BodyLengthLimit = MaxPartBytes };

        ParserConvertResult? result = null;
        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            var id = section.Headers?.TryGetValue("Content-ID", out var value) == true ? value.ToString() : null;
            if (string.Equals(id, "result", StringComparison.Ordinal))
            {
                result = await JsonSerializer.DeserializeAsync<ParserConvertResult>(section.Body, _json, cancellationToken);
            }
            else if (string.Equals(id, "docling", StringComparison.Ordinal) && doclingGzip is not null)
            {
                await section.Body.CopyToAsync(doclingGzip, cancellationToken);
            }
            else
            {
                await section.Body.CopyToAsync(Stream.Null, cancellationToken);
            }
        }

        return result ?? throw new InvalidDataException("The parser's answer has no result part.");
    }

    public async Task<ParserInfo> GetInfoAsync(CancellationToken cancellationToken) =>
        await http.GetFromJsonAsync<ParserInfo>(InfoPath, _json, cancellationToken)
        ?? throw new InvalidDataException("The parser's /v1/info answer is empty.");

    private async Task<ParserException> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ParserErrorResponse? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ParserErrorResponse>(_json, cancellationToken);
        }
        catch (JsonException)
        {
            // Not the contract's error body (a proxy, or a crash before the handler): the status code is all there is.
        }

        var retryAfter = response.Headers.RetryAfter?.Delta;
        return new ParserException(response.StatusCode, error, retryAfter);
    }
}

/// <summary>A non-200 answer of the parser; <see cref="Error"/> is the contract's error body when it sent one.</summary>
public sealed class ParserException : Exception
{
    public ParserException()
    {
    }

    public ParserException(string message)
        : base(message)
    {
    }

    public ParserException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ParserException(HttpStatusCode statusCode, ParserErrorResponse? error, TimeSpan? retryAfter)
        : base($"The parser answered {(int)statusCode} {error?.Error.Code ?? "without an error body"}.")
    {
        StatusCode = statusCode;
        Error = error;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode StatusCode { get; }

    public ParserErrorResponse? Error { get; }

    /// <summary>From <c>Retry-After</c> on <c>busy</c> and <c>not_ready</c> (503).</summary>
    public TimeSpan? RetryAfter { get; }
}
