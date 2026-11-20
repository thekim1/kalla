```

BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
13th Gen Intel Core i7-13700H 2.92GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                                   | Assistants | Mean         | Error      | StdDev     | Gen0   | Allocated |
|----------------------------------------- |----------- |-------------:|-----------:|-----------:|-------:|----------:|
| **&#39;Hash presented API key (HMAC)&#39;**          | **10**         |    **920.39 ns** |  **12.079 ns** |  **11.299 ns** | **0.0057** |     **288 B** |
| &#39;Decide: chat on one assistant&#39;          | 10         |     60.59 ns |   0.618 ns |   0.548 ns | 0.0020 |      96 B |
| &#39;Filter: /v1/models over all assistants&#39; | 10         |    658.57 ns |   6.332 ns |   5.613 ns | 0.0210 |    1000 B |
| **&#39;Hash presented API key (HMAC)&#39;**          | **1000**       |    **916.16 ns** |  **13.818 ns** |  **12.250 ns** | **0.0057** |     **288 B** |
| &#39;Decide: chat on one assistant&#39;          | 1000       |     58.41 ns |   0.888 ns |   0.831 ns | 0.0020 |      96 B |
| &#39;Filter: /v1/models over all assistants&#39; | 1000       | 63,394.60 ns | 925.279 ns | 865.507 ns | 1.4648 |   72568 B |
