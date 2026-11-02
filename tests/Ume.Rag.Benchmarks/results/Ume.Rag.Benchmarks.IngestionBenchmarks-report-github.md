```

BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
13th Gen Intel Core i7-13700H 2.92GHz, 1 CPU, 20 logical and 10 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                                         | Page                 | Mean          | Error         | StdDev        | Gen0     | Gen1    | Gen2    | Allocated  |
|----------------------------------------------- |--------------------- |--------------:|--------------:|--------------:|---------:|--------:|--------:|-----------:|
| **&#39;Whole page: detect → … → personal data&#39;**       | **hours(...).html [24]** |  **3,353.522 μs** |    **66.9584 μs** |   **171.6399 μs** |  **31.2500** |       **-** |       **-** |  **2501569 B** |
| &#39;Chunk the cleaned page&#39;                       | hours(...).html [24] |    819.433 μs |    15.5238 μs |    15.2464 μs |   8.7891 |       - |       - |   427584 B |
| &#39;Personal-data patterns over the page text&#39;    | hours(...).html [24] |     53.947 μs |     1.0767 μs |     1.2817 μs |        - |       - |       - |     1080 B |
| &#39;BGE-M3 token count of the page text&#39;          | hours(...).html [24] |     30.802 μs |     0.5552 μs |     0.5193 μs |        - |       - |       - |          - |
| &#39;Decompounder parts of one chunk (SearchText)&#39; | hours(...).html [24] |    181.878 μs |     3.6171 μs |     4.5745 μs |   5.6152 |       - |       - |   268640 B |
| &#39;Language detection&#39;                           | hours(...).html [24] |      6.939 μs |     0.1337 μs |     0.1592 μs |   0.2213 |       - |       - |    10712 B |
| **&#39;Whole page: detect → … → personal data&#39;**       | **pdf-l(...).html [30]** |  **9,355.381 μs** |   **183.6173 μs** |   **257.4059 μs** |  **78.1250** | **31.2500** | **31.2500** |  **7943209 B** |
| &#39;Chunk the cleaned page&#39;                       | pdf-l(...).html [30] |    329.623 μs |     4.8641 μs |     4.0617 μs |   5.3711 |       - |       - |   263240 B |
| &#39;Personal-data patterns over the page text&#39;    | pdf-l(...).html [30] |     32.180 μs |     0.6375 μs |     0.6261 μs |        - |       - |       - |      632 B |
| &#39;BGE-M3 token count of the page text&#39;          | pdf-l(...).html [30] |     17.538 μs |     0.3397 μs |     0.3912 μs |        - |       - |       - |          - |
| &#39;Decompounder parts of one chunk (SearchText)&#39; | pdf-l(...).html [30] |     90.482 μs |     1.2810 μs |     1.1983 μs |   2.9297 |       - |       - |   141640 B |
| &#39;Language detection&#39;                           | pdf-l(...).html [30] |      4.146 μs |     0.0788 μs |     0.0737 μs |   0.1297 |       - |       - |     6408 B |
| **&#39;Whole page: detect → … → personal data&#39;**       | **regul(...).html [22]** | **59,257.703 μs** | **1,181.2864 μs** | **1,213.0942 μs** | **666.6667** |       **-** |       **-** | **35527115 B** |
| &#39;Chunk the cleaned page&#39;                       | regul(...).html [22] | 46,767.918 μs |   904.8497 μs | 1,207.9485 μs | 545.4545 |       - |       - | 29234360 B |
| &#39;Personal-data patterns over the page text&#39;    | regul(...).html [22] |  3,619.704 μs |    57.6847 μs |    51.1360 μs |        - |       - |       - |      632 B |
| &#39;BGE-M3 token count of the page text&#39;          | regul(...).html [22] |  2,472.656 μs |    48.8475 μs |    76.0497 μs |        - |       - |       - |          - |
| &#39;Decompounder parts of one chunk (SearchText)&#39; | regul(...).html [22] |     11.461 μs |     0.1181 μs |     0.1047 μs |   0.4730 |       - |       - |    23008 B |
| &#39;Language detection&#39;                           | regul(...).html [22] |     36.968 μs |     0.7132 μs |     0.9522 μs |   1.2817 |       - |       - |    62104 B |
