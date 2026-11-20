using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Kalla.Answering.Chat;
using Kalla.Domain.Retrieval;

namespace Kalla.Benchmarks;

/// <summary>
/// The once-per-turn .NET work of a chat request around retrieval (chat design §6–§9): parsing an Open WebUI-shaped
/// request with history, the language guess, building the prompt from 10 retrieved chunks (≈ 4,000 tokens of sources)
/// and history, and applying citations to a whole (non-streamed) answer.
/// </summary>
[MemoryDiagnoser]
public class ChatTurnBenchmarks
{
    private JsonElement _request;
    private List<RetrievedChunk> _chunks = null!;
    private List<ChatMessageIn> _history = null!;
    private IReadOnlyList<PromptSource> _sources = null!;
    private string _answer = null!;
    private const string Question = "Behöver jag bygglov för att bygga en altan som är högre än 1,8 meter?";

    [GlobalSetup]
    public void Setup()
    {
        _history = [.. Enumerable.Range(0, 10).Select(i => new ChatMessageIn(i % 2 == 0 ? "user" : "assistant", BenchData.SwedishText(i % 2 == 0 ? 20 : 150, i) + " [1]"))];
        var messages = _history.Select(m => new { role = m.Role, content = m.Text }).Append(new { role = "user", content = Question });
        _request = JsonSerializer.SerializeToElement(new { model = "bygglovsassistenten", stream = true, messages, stream_options = new { include_usage = true } });
        _chunks = PromptData.Chunks(10);
        _sources = PromptData.Sources(5);
        _answer = string.Concat(PromptData.AnswerDeltas(250));
    }

    [Benchmark(Description = "Parse request (11 messages)")]
    public ParsedChat ParseRequest() => ChatRequestParser.Parse(_request);

    [Benchmark(Description = "Language guess")]
    public string Language() => LanguageGuess.Detect(Question);

    [Benchmark(Description = "Build prompt (10 chunks, 10 history)")]
    public BuiltPrompt BuildPrompt() => PromptBuilder.Build(PromptKind.Sources,
        new PromptContext("Bygglovsassistenten", "Svara kort och vänligt.", LanguageGuess.Swedish, Question, _history, AssistantChatSettings.Default), _chunks);

    [Benchmark(Description = "Citations on a whole answer (non-streamed)")]
    public CitedAnswer Citations() => Kalla.Answering.Chat.Citations.Apply(_answer, _sources);
}
