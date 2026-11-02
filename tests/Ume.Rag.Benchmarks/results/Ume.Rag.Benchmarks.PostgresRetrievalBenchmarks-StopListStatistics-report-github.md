```

BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
13th Gen Intel Core i7-13700H 2.92GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 10.0.401
  [Host] : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=InProcess  Toolchain=InProcessEmitToolchain  IterationCount=15  
WarmupCount=3  

```
| Method                           | Chunks | Mean      | Error    | StdDev   | Allocated |
|--------------------------------- |------- |----------:|---------:|---------:|----------:|
| **&#39;Stop-list statistics (ts_stat)&#39;** | **10000**  |  **39.09 ms** | **0.308 ms** | **0.273 ms** |    **8.1 KB** |
| **&#39;Stop-list statistics (ts_stat)&#39;** | **50000**  | **249.61 ms** | **3.947 ms** | **3.296 ms** |  **11.23 KB** |
