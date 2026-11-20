```

BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
13th Gen Intel Core i7-13700H 2.92GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                                       | Mean      | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|--------------------------------------------- |----------:|----------:|----------:|-------:|-------:|----------:|
| &#39;Parse request (11 messages)&#39;                | 11.247 μs | 0.1275 μs | 0.1065 μs | 0.3815 | 0.0153 |  17.97 KB |
| &#39;Language guess&#39;                             |  1.381 μs | 0.0185 μs | 0.0164 μs | 0.0305 |      - |   1.48 KB |
| &#39;Build prompt (10 chunks, 10 history)&#39;       | 90.847 μs | 1.6026 μs | 1.5740 μs | 6.1035 | 0.4883 | 279.58 KB |
| &#39;Citations on a whole answer (non-streamed)&#39; | 17.968 μs | 0.3569 μs | 0.3506 μs | 0.9766 | 0.0305 |  47.31 KB |
