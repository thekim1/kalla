```

BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
13th Gen Intel Core i7-13700H 2.92GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                                   | CandidatesPerList | Mean         | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|----------------------------------------- |------------------ |-------------:|------------:|------------:|-------:|-------:|----------:|
| **&#39;Normalize query (NFC, whitespace)&#39;**      | **100**               |     **501.6 ns** |     **7.98 ns** |     **7.47 ns** | **0.0181** |      **-** |     **864 B** |
| &#39;Keyword search text (decompounder)&#39;     | 100               |  18,138.0 ns |   219.29 ns |   183.12 ns | 0.7935 |      - |   37800 B |
| &#39;Materialise candidates (both lists)&#39;    | 100               |  31,359.2 ns |   626.25 ns | 1,265.06 ns | 4.1809 |      - |  198400 B |
| &#39;RRF fusion, no adjusters&#39;               | 100               |  20,303.0 ns |   398.21 ns |   408.93 ns | 1.2512 | 0.0916 |   60584 B |
| &#39;RRF fusion + decay + demote&#39;            | 100               |  47,863.4 ns |   821.81 ns |   768.73 ns | 2.0752 | 0.1831 |   99984 B |
| &#39;Build adjusters for the scope&#39;          | 100               |     166.0 ns |     3.34 ns |     3.28 ns | 0.0126 |      - |     608 B |
| &#39;Dedup (text, identity group)&#39;           | 100               |  12,043.0 ns |   107.62 ns |    95.40 ns | 0.2899 |      - |   14208 B |
| &#39;Per-document cap, top 10&#39;               | 100               |     437.1 ns |     7.91 ns |     7.01 ns | 0.0134 |      - |     640 B |
| Coverage                                 | 100               |   4,088.2 ns |    61.82 ns |    57.83 ns | 0.1297 |      - |    6376 B |
| &#39;Engine search, hybrid, in-memory index&#39; | 100               |  88,483.5 ns |   587.91 ns |   521.17 ns | 3.4180 | 0.2441 |  165216 B |
| **&#39;Normalize query (NFC, whitespace)&#39;**      | **200**               |     **496.5 ns** |     **8.45 ns** |     **7.90 ns** | **0.0172** |      **-** |     **864 B** |
| &#39;Keyword search text (decompounder)&#39;     | 200               |  18,313.3 ns |   357.43 ns |   367.05 ns | 0.7629 |      - |   37800 B |
| &#39;Materialise candidates (both lists)&#39;    | 200               |  63,452.1 ns | 1,255.98 ns | 3,264.46 ns | 8.3008 |      - |  396800 B |
| &#39;RRF fusion, no adjusters&#39;               | 200               |  39,749.6 ns |   655.48 ns |   581.07 ns | 2.5024 | 0.1221 |  121552 B |
| &#39;RRF fusion + decay + demote&#39;            | 200               |  97,087.2 ns |   807.56 ns |   674.35 ns | 4.1504 | 0.4883 |  200312 B |
| &#39;Build adjusters for the scope&#39;          | 200               |     168.0 ns |     3.25 ns |     3.34 ns | 0.0126 |      - |     608 B |
| &#39;Dedup (text, identity group)&#39;           | 200               |  25,133.7 ns |   409.85 ns |   383.37 ns | 0.6409 | 0.0305 |   30256 B |
| &#39;Per-document cap, top 10&#39;               | 200               |     432.6 ns |     3.82 ns |     3.57 ns | 0.0134 |      - |     640 B |
| Coverage                                 | 200               |   7,612.1 ns |    57.37 ns |    53.66 ns | 0.2136 |      - |   10520 B |
| &#39;Engine search, hybrid, in-memory index&#39; | 200               | 162,568.5 ns | 1,932.77 ns | 1,807.92 ns | 5.8594 | 0.4883 |  285736 B |
