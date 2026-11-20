- **Ume.Rag is now Källa** (#899, Decisions "Product names"): the platform is called Källa (Kalla in ASCII) and the
  gateway Fors. The projects are `Kalla.*` (`Kalla.slnx`), the images `ghcr.io/thekim1/kalla/*`, the variables
  `KALLA_*` (the gateway's `FORS_*`), the services, volumes and networks `kalla-*`, the database roles `kalla_*`, and
  the repository thekim1/kalla (`https://thekim1.github.io/kalla/`). The chat extension is `kalla` (`kalla.topics`,
  `kalla.related_documents`, …) and its metric `rag.chat.kalla`. New keys are `kalla-sk-…` and `kalla-pk-…`; keys
  issued before the rename (`ume-rag-sk-…`) keep working. Källa calls the gateway with the `x-fors-*` headers and
  `/v1/fors/context` and reads the `fors` model facts, so it needs **Fors 0.12** or later. Examples and the demo
  content stay Umeå kommun.
- **Licence:** Apache-2.0, copyright Kim Vallén and Nichlas Löfdahl. Releases up to and including 0.8.0 were published
  under EUPL-1.2.
