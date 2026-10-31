# BGE-M3 SentencePiece model

`sentencepiece.bpe.model` is the tokenizer model of BGE-M3, embedded in `Ume.Rag.Worker` for token counting
(processing design §6.1). Despite the file name its `TrainerSpec.model_type` is UNIGRAM (250,000 pieces, `nmt_nfkc`).

| Field | Value |
|---|---|
| Source | https://huggingface.co/BAAI/bge-m3/resolve/5617a9f61b028005a4858fdac845db406aefb181/sentencepiece.bpe.model |
| Revision | `5617a9f61b028005a4858fdac845db406aefb181` (2024-07-03) |
| Licence | MIT (model card of `BAAI/bge-m3`) |
| SHA-256 | `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865` (checked when the counter loads) |
| Bytes | 5,069,051 |

Count parity with Hugging Face's tokenizer for the same revision: `tests/fixtures/processing/tokenizer/` (to-verify #26).
