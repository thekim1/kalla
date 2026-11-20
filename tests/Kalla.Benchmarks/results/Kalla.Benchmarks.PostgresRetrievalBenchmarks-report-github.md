```

BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
13th Gen Intel Core i7-13700H 2.92GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 10.0.401
  [Host] : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  IterationCount=15  
WarmupCount=3  

```
| Method                                      | Chunks | Mean         | Error       | StdDev      | Gen0   | Allocated |
|-------------------------------------------- |------- |-------------:|------------:|------------:|-------:|----------:|
| **&#39;K: rare terms&#39;**                             | **10000**  |   **1,935.7 μs** |    **24.27 μs** |    **21.51 μs** |      **-** | **130.91 KB** |
| &#39;K: common terms (stop list applied)&#39;       | 10000  |     876.1 μs |    14.94 μs |    13.24 μs | 0.9766 |  47.87 KB |
| &#39;K: common terms, no stop list&#39;             | 10000  |  81,525.7 μs | 1,563.31 μs | 1,305.43 μs |      - | 131.52 KB |
| &#39;V: exact (top-N sort)&#39;                     | 10000  |  26,350.5 μs | 1,459.71 μs | 1,365.41 μs |      - | 130.42 KB |
| &#39;V: approximate (HNSW halfvec)&#39;             | 10000  |   2,331.3 μs |    95.92 μs |    89.72 μs |      - | 130.27 KB |
| &#39;Hydrate 10 hits ± 1 neighbour&#39;             | 10000  |     997.3 μs |    51.67 μs |    45.81 μs |      - |   75.8 KB |
| &#39;Engine: hybrid search (no embedding call)&#39; | 10000  |  25,444.5 μs |   445.70 μs |   416.91 μs |      - | 372.45 KB |
| **&#39;K: rare terms&#39;**                             | **50000**  |   **6,962.4 μs** |    **82.01 μs** |    **68.48 μs** |      **-** |  **130.9 KB** |
| &#39;K: common terms (stop list applied)&#39;       | 50000  |   1,877.0 μs |    23.62 μs |    20.94 μs |      - | 131.39 KB |
| &#39;K: common terms, no stop list&#39;             | 50000  | 453,569.4 μs | 5,729.57 μs | 4,473.27 μs |      - |  138.9 KB |
| &#39;V: exact (top-N sort)&#39;                     | 50000  |  76,983.9 μs | 1,308.35 μs | 1,092.53 μs |      - | 132.07 KB |
| &#39;V: approximate (HNSW halfvec)&#39;             | 50000  |   2,077.0 μs |    40.95 μs |    34.19 μs |      - | 131.29 KB |
| &#39;Hydrate 10 hits ± 1 neighbour&#39;             | 50000  |     733.7 μs |    24.34 μs |    22.77 μs | 0.9766 |  77.38 KB |
| &#39;Engine: hybrid search (no embedding call)&#39; | 50000  |   3,587.5 μs |    40.57 μs |    37.95 μs | 7.8125 |  378.9 KB |
