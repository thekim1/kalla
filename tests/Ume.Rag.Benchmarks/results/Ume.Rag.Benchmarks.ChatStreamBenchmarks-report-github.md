```

BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
13th Gen Intel Core i7-13700H 2.92GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                                                                        | Categories | Mean       | Error    | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------------------------------------------------------------------ |----------- |-----------:|---------:|---------:|------:|--------:|--------:|----------:|------------:|
| &#39;Downstream: 400 SSE chunks (current: anonymous object → string)&#39;             | Downstream |   427.5 μs |  8.00 μs |  8.56 μs |  1.00 |    0.03 | 18.5547 |  890336 B |       1.000 |
| &#39;Downstream: 400 SSE chunks (candidate: Utf8JsonWriter, reused buffer)&#39;       | Downstream |   101.0 μs |  1.96 μs |  2.61 μs |  0.24 |    0.01 |       - |     704 B |       0.001 |
|                                                                               |            |            |          |          |       |         |         |           |             |
| &#39;Answer transducer: feed 400 deltas + flush&#39;                                  | Transducer |   316.6 μs |  4.08 μs |  3.81 μs |     ? |       ? | 14.1602 |  688456 B |           ? |
|                                                                               |            |            |          |          |       |         |         |           |             |
| &#39;Upstream: read 400 SSE lines (current: CTS per line + JsonNode)&#39;             | Upstream   | 1,110.8 μs | 22.21 μs | 22.81 μs |  1.00 |    0.03 | 42.9688 | 2083992 B |        1.00 |
| &#39;Upstream: read 400 SSE lines (candidate: one CTS re-armed + Utf8JsonReader)&#39; | Upstream   |   137.5 μs |  2.74 μs |  3.75 μs |  0.12 |    0.00 |  0.2441 |   22560 B |        0.01 |
