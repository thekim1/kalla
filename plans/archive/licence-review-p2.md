# Licence review for register item P2 (parser image, models, data, sv_SE word list)

Date: 2026-10-30. Scope: `plans/postponed-decisions.md:27` (P2). Every item checked against the primary source:
upstream LICENSE files at the pinned tag, Hugging Face model metadata at the pinned revision, Debian copyright files,
the wheels pinned in `src/parser/uv.lock`, and the data publishers' own catalogues. Wheels were downloaded and read
(unzip, `strings`), never executed. The Debian set was read from the locally built parser image
(`a072ffaf0447`, built 2026-10-30 16:07 from the current Dockerfile) with `docker create` + `docker cp` (no container
was started). This is an engineering review, not legal advice.

Legend: **OK** nothing to do. **OK with obligations** usable, but notices or source offers are owed when we
distribute. **BLOCKER** cannot be used as planned. **UNCLEAR** needs an answer from someone else.

Two facts frame all of the (a)/(b) columns:

- (a) Internal production use by Umeå kommun is not "distribution" or "conveying". No GPL/LGPL/MPL/Apache/MIT
  obligation triggers on internal use, and none of the licences below is network-copyleft (no AGPL, no SSPL).
- (b) Handing images or bundles to another municipality is distribution. Then notices, licence texts and (for
  GPL/LGPL parts) source availability are owed.

## Summary

| # | Item | Version / revision | Licence (SPDX) | (a) internal | (b) distribution | Verdict |
|---|---|---|---|---|---|---|
| 1a | Docling (`docling-slim`) | 2.142.0 | MIT | OK | MIT notice | OK with obligations |
| 1b | `docling-core`, `docling-ibm-models` | 2.104.0, 4.0.4 | MIT | OK | MIT notice | OK with obligations |
| 1c | `docling-parse` (incl. statically linked qpdf, FreeType, libjpeg-turbo, OpenJPEG, lcms2, blend2d, utf8, json, loguru; Adobe cmaps; AFM metrics) | 7.23.0 | MIT (wheel ships only its own MIT text) | OK | MIT + third-party notices that the wheel does **not** ship (FreeType FTL credit, IJG, qpdf Apache-2.0, Adobe BSD-3) | OK with obligations |
| 1d | Layout model Heron | `docling-project/docling-layout-heron@8f39ad3c` | Apache-2.0 | OK | Apache-2.0 text in the image | OK with obligations |
| 1e | TableFormer (fast + accurate) | `docling-project/docling-models@fc0f2d45` (v2.3.0) | CDLA-Permissive-2.0 | OK | CDLA-P-2.0 text with the weights | OK with obligations |
| 1f | pypdfium2 | 5.14.0 | Apache-2.0 OR BSD-3-Clause (code); CC-BY-4.0 (docs only) | OK | licence files ship in the wheel | OK with obligations |
| 1g | PDFium inside pypdfium2 | pdfium-binaries 156.0.8076 | BSD-3-Clause + Apache-2.0; deps: FreeType (FTL), ICU (Unicode), libjpeg-turbo (IJG/BSD-3), OpenJPEG (BSD-2), lcms2 (MIT), libpng, zlib, abseil, AGG, simdutf, fast_float, llvm-libc | OK | notices ship in the wheel (`BUILD_LICENSES/`) | OK with obligations |
| 2a | KB NER model `KBLab/bert-base-swedish-cased-ner` | `f3c83af8` | MIT (HF metadata only; no LICENSE file in the repo) | OK | MIT notice naming KB | OK with obligations |
| 2b | SUC 3.0 terms for the NER model's training data | SUC 3.0 (Språkbanken, DOI 10.23695/wy84-ar30) | full corpus: signed research licence; scrambled SUCX 3.0: CC-BY-4.0 | OK: the model is a research *result*, which the SUC agreement leaves "totally at [the researcher's] disposal"; we never receive SUC texts | OK; credit SUC/Språkbanken as courtesy | OK with obligations |
| 3a | Tesseract (Debian `tesseract-ocr`, `libtesseract5`) | 5.5.0-1+b1 | Apache-2.0 | OK | Debian copyright files already in the image | OK with obligations |
| 3b | `swe` traineddata (Debian `tesseract-ocr-swe`) | 1:4.1.0-2 (upstream **tessdata_fast**) | Apache-2.0 | OK | as 3a | OK with obligations |
| 4a | SCB name statistics (gazetteer) | Statistikdatabasen BE0001FNamn10 / BE0001ENamn10, 1999-2020, frozen | CC0-1.0 | OK | OK | OK (note: SCB stopped name statistics in 2024) |
| 4b | Skatteverket test personnummer / samordningsnummer | EntryScape datasets, updated each December | CC0-1.0 + condition "får bara användas i testmiljöer" | OK in tests and fixtures only | OK | OK with obligations (test environments only) |
| 5 | Google robotstxt test cases | `google/robotstxt@22b355ff` (master, 2026-04-01) | Apache-2.0 | OK | keep Google copyright + Apache header, mark changes | OK with obligations |
| 6 | `sv_SE` Hunspell word list (Den stora svenska ordlistan 2.42) | `LibreOffice/dictionaries@25d77930`, SHA-256 matches | **LGPL-3.0-only** (not a tri-licence) | OK | LGPL-3 §4 duties; embedding as a resource makes "replace the Library" awkward | OK with obligations (change recommended before any distribution) |
| 7a | Rest of the Python set (85 packages in the image, 113 in the lock incl. dev) | `uv.lock` | permissive (MIT, BSD, Apache-2.0, PSF, ISC, MIT-CMU, CNRI-Python); weak copyleft: certifi MPL-2.0, tqdm MPL-2.0 AND MIT | OK | licence files ship in each `dist-info` | OK with obligations |
| 7b | LGPL/GPL-with-exception binaries bundled in wheels: FFmpeg (opencv), libiconv (lxml, static), libquadmath, libgfortran, libgomp (numpy, scipy, opencv, torch) | see details | LGPL-2.1-or-later; GPL-3.0-or-later WITH GCC-exception-3.1 | OK | LGPL text + source offer/pointer | OK with obligations |
| 7c | Debian packages in the parser image (159) | snapshot 20261022T000000Z | Debian main (DFSG); GPL/LGPL tools and libraries from the base image and Tesseract's dependency chain (incl. `libjbig0` GPL-2.0-or-later) | OK | GPL/LGPL source availability (snapshot.debian.org pointer or written offer) | OK with obligations |

**No BLOCKER.** **No UNCLEAR item among the P2 licences.** Three side findings are listed at the end (not licence
blockers): the platform's own code has no licence yet (decides how "distribution to other municipalities" works at
all), opencv-python-headless bundles end-of-life OpenSSL 1.1.1k, and SCB's name statistics end at 2020.

---

## 1. Docling, its models, docling-parse, pypdfium2 and PDFium

What we pin:
- `src/parser/pyproject.toml:6` `docling-slim[convert-core,format-pdf,format-office,format-opendocument,models-local]==2.142.0`, `:9` `pypdfium2==5.14.0`; lock: `docling-core 2.104.0`, `docling-ibm-models 4.0.4`, `docling-parse 7.23.0`.
- `src/parser/ume_parser/model_manifest.json:5-6, :17-18, :30-31`, fetched by `src/parser/scripts/download_models.py` (only the files listed: weights, configs, tokenizer files; **no README or licence file**).

### 1a/1b Docling, docling-core, docling-ibm-models: MIT
- `https://raw.githubusercontent.com/docling-project/docling/v2.142.0/LICENSE`: "MIT License, Copyright The Docling Contributors". `docling-slim` is published from the same repository (PyPI metadata `project_urls.repository = github.com/docling-project/docling`, `License-Expression: MIT`).
- `docling-ibm-models@v4.0.4/LICENSE`: MIT, "Copyright (c) 2024 International Business Machines". `docling-core` 2.104.0: PyPI `License-Expression: MIT`.
- Obligation: keep the MIT notice (already inside each `dist-info` in `/opt/venv`).

### 1c docling-parse 7.23.0: MIT, plus statically linked third-party code without notices
- `docling-parse@v7.23.0/LICENSE`: MIT (template text "Copyright (c) [year] [fullname]", i.e. no named holder; treat as IBM / Docling contributors). Wheel `docling_parse-7.23.0-cp312-cp312-manylinux_2_27_x86_64.manylinux_2_28_x86_64.whl` ships only `dist-info/licenses/LICENSE` and `pdf_resources/cmap-resources/LICENSE.md` (Adobe, BSD-3-Clause).
- The extension `pdf_parsers.cpython-312-x86_64-linux-gnu.so` statically includes, per `CMakeLists.txt:132-141` at the tag and `cmake/extlib_*.cmake`: qpdf v12 (Apache-2.0; ~1 000 "QPDF" strings in the `.so`), FreeType (FTL or GPL-2.0, we can choose FTL), libjpeg-turbo (IJG + BSD-3-Clause + Zlib), OpenJPEG (BSD-2-Clause), lcms2 (MIT), blend2d + asmjit (Zlib), utf8 (BSL-1.0), nlohmann json (MIT), cxxopts (MIT), loguru (public domain), pybind11 (BSD-3-Clause), PDFium's jbig2 code (BSD-3-Clause). All permissive; none copyleft.
- The wheel does **not** carry these notices. When we distribute the image we must add: FreeType FTL credit ("Portions of this software are copyright © The FreeType Project (www.freetype.org). All rights reserved."), IJG statement ("This software is based in part on the work of the Independent JPEG Group"), the qpdf Apache-2.0 text, and the BSD/MIT/Zlib/BSL texts.
- `pdf_resources/fonts/` holds 213 `.afm` font-metric files (no font outlines). Adobe Core 14 AFMs are freely redistributable; the `custom/Microsoft/*.afm` files carry "Notice Typeface © The Monotype Corporation plc ... All Rights Reserved". These are metric tables, IBM redistributes them inside an MIT package, and we do not modify or extract them. Low risk; noted, not a blocker.
- (a) OK. (b) OK with the notices above.

### 1d Layout model Heron: Apache-2.0
- HF API `https://huggingface.co/api/models/docling-project/docling-layout-heron/revision/8f39ad3c0b4c58e9c2d2c84a38465abf757272d8`: `cardData.license = apache-2.0`, tag `license:apache-2.0`. README at that revision, line 2: `license: apache-2.0`. No NOTICE file in the repo (siblings: `.gitattributes, README.md, config.json, docling_heron_400.png, model.safetensors, preprocessor_config.json`).
- (a) OK. (b) Apache-2.0 §4: ship a copy of the licence with the weights. The image currently ships only `config.json`, `model.safetensors`, `preprocessor_config.json`.

### 1e TableFormer: CDLA-Permissive-2.0
- HF API for `docling-project/docling-models` at `fc0f2d45e2218ea24bce5045f58a389aed16dc23`: `license: cdla-permissive-2.0`; README line 2 the same. Only the TableFormer files are downloaded (manifest lines 17-26).
- CDLA-Permissive-2.0: use, modify and share; when sharing the Data (the weights), make the licence text available. Results (model outputs) carry no restriction.
- (a) OK. (b) ship the CDLA-Permissive-2.0 text next to the weights.

### 1f/1g pypdfium2 5.14.0 and PDFium
- Wheel `pypdfium2-5.14.0-py3-none-manylinux_2_17_x86_64.manylinux2014_x86_64.whl`, METADATA: "pypdfium2 itself is available by the terms and conditions of Apache-2.0 / BSD-3-Clause. Documentation and examples ... CC-BY-4.0." (`License: BSD-3-Clause, Apache-2.0, dependency licenses`).
- Bundled `pypdfium2_raw/libpdfium.so`, `version.json`: `{"major":156,"build":8076,"origin":"pdfium-binaries"}`. PDFium licence (`BUILD_LICENSES/pdfium.txt`): BSD-3-Clause ("Copyright 2014 The PDFium Authors") followed by Apache-2.0. The pdfium-binaries build scripts are MIT (Benoit Blanchon).
- The wheel ships every dependency licence in `dist-info/licenses/data/linux_x64/BUILD_LICENSES/`: abseil, agg23, fast_float, freetype (FTL/GPL-2 dual; FTL applies), icu, lcms, libjpeg_turbo (IJG + md), libopenjpeg, libpng, llvm-libc, pdfium-binaries, pdfium, simdutf, zlib. All permissive.
- (a) OK. (b) notices already in the image; include them in our NOTICE bundle.

## 2. KB BERT NER and SUC 3.0

### 2a Model licence: MIT
- HF API `KBLab/bert-base-swedish-cased-ner` at `f3c83af8196b812028fc08e1971a2d289d0a3f1d`: `cardData.license = mit`; README lines 1-4 `language: sv`, `license: mit`. The repo has **no LICENSE file**, and the GitHub repo `Kungbib/swedish-bert-models` (last push 2020-10-15) has no licence file either (GitHub API `license: null`, `LICENSE` 404). The MIT grant rests on the model-card metadata.
- README line 13: "bert-base-swedish-cased-ner (*experimental*) - a BERT fine-tuned for NER using SUC 3.0". Line 119: "Resources from Stockholms University, Umeå University and Swedish Language Bank at Gothenburg University were used when fine-tuning BERT for NER."
- Obligation: MIT needs the copyright and permission notice with copies. Write it with the holder "National Library of Sweden / KBLab" and the MIT text, since the repo provides none.

### 2b SUC 3.0 terms: may a model trained on it be used in municipal production?
Primary sources:
- `https://spraakbanken.gu.se/en/resources/suc3`: the full SUC 3.0 is "freely available for research; require a signed licence" (Appendix 3 of the SUC licence, sent to Språkbanken). The scrambled corpus (`suc3.xml.bz2`, sentences in scrambled order) and word statistics are listed as **CC-BY-4.0**. Citation: "Språkbanken (2024). SUC 3.0 (updated: 2024-06-03). [Data set]. DOI 10.23695/wy84-ar30".
- The licence itself, `https://spraakbanken.gu.se/sites/default/files/2022/suc-license.pdf` (8 pages, Swedish and English):
  - §1: the rights holders grant IL (Stockholm and Umeå linguistics departments) use "solely for research purposes"; "IL undertakes not to use the acquired texts for any commercial purposes whatsoever"; "The Holder of the Right has no right whatsoever to any research results."
  - Appendix 2: results are "word lists, statistical tabulations and other kinds of classifications, where it will be impossible to identify the texts making them up. Another type of results will consist in computer programs written for analytical purposes."
  - Appendix 3 (the researcher's declaration): "I further undertake not to distribute or provide access to any copy of the machine-readable texts. My analytical and other research results are, however, totally at my disposal." and "In the event of publication of the results based on this text corpus the source shall be stated."

Assessment: the restrictions bind the **texts** (no redistribution, no commercial use of the texts). A fine-tuned NER model is a research result from which the texts cannot be identified, and the agreement leaves results at the researcher's disposal; KB released it under MIT. We never receive or redistribute SUC texts. Municipal use is not commercial use of the texts anyway, and the rights holders expressly have no rights in results. Using the model in a municipal production service is therefore consistent with the SUC terms.
- (a) OK. (b) OK. Attribution: the SUC "source shall be stated" duty is the publishing researcher's; we add a courtesy line (SUC 3.0, Språkbanken, DOI) to the NOTICE. Residual risk is low; asking KBLab is not needed for the pilot.
- Not a licence issue but relevant: the card calls the model "experimental" and says "These labels are subject to change" (README lines 13, 67). Already handled by the pinned revision and P1 measurement.

## 3. Tesseract and `swe`

- Dockerfile `src/parser/Dockerfile:48-50`: `tini=0.19.0-3+b8`, `tesseract-ocr=5.5.0-1+b1`, `tesseract-ocr-swe=1:4.1.0-2`; snapshot `20261022T000000Z` (`:31`). The image's dpkg status confirms these versions plus `libtesseract5=5.5.0-1+b1`, `libleptonica6=1.84.1-4`, `tesseract-ocr-eng`/`-osd=1:4.1.0-2`.
- `https://sources.debian.org/data/main/t/tesseract/5.5.0-1/debian/copyright`: `Files: *` Apache-2.0 (HP 1988-1995, Google 2006-2024); `nsis/find_deps.py` MIT.
- `https://sources.debian.org/data/main/t/tesseract-lang/1:4.1.0-2/debian/copyright`: `Upstream-Name: tessdata_fast`, `Source: https://github.com/tesseract-ocr/tessdata_fast/`, `Files: *` Apache-2.0. This also answers the "`tessdata_fast` or `best`, *to verify*" in `plans/parser/design.md` §9.2: Debian's `tesseract-ocr-swe` is **tessdata_fast**.
- Leptonica (`/usr/share/doc/libleptonica6/copyright`): BSD-2-Clause style. tini: Expat (MIT).
- Dependency chain: `tesseract-ocr` depends on cairo/pango/glib/harfbuzz/ICU/curl; leptonica on libtiff6, which depends on **`libjbig0` (GPL-2.0-or-later)**. Tesseract runs as a separate CLI process (`TesseractCliOcrOptions`), so our Python code is not linked to it.
- (a) OK. (b) the Debian copyright files are already in `/usr/share/doc/*/copyright`; GPL/LGPL source availability as in 7c. The Apache-2.0/GPL-2 mix through libjbig is Debian's packaging and is shipped by Debian unchanged; not ours to fix.

## 4. SCB name statistics and Skatteverket test numbers

### 4a SCB name statistics: CC0-1.0
- `https://www.scb.se/vara-tjanster/oppna-data/`: "Vi använder licensen CC0 för dessa data", use "utan krav på att ange källa"; suggested credit "Källa: SCB" / "Source: Statistics Sweden"; "if you have processed the data yourself ... you should not state SCB as the source" (also on `https://www.scb.se/om-scb/om-scb.se-och-anvandningsvillkor/`).
- Tables: `https://www.statistikdatabasen.scb.se/goto/en/ssd/BE0001FNamn10` "First names with at least 10 bearers ... 1999 - 2020", last updated 2021-03-01, bulk CSV; surnames `BE0001ENamn10`.
- `https://www.scb.se/namnsok/`: "Från och med 2024 slutar vi producera namnstatistik ... hänvisar vi till Skatteverket"; next publication "Ingen planerad". Skatteverket's open data has only newborn top lists (`Namn på nyfödda`, 2021 onwards), not full bearer counts.
- (a)(b) OK. Attribution: none required. Because our gazetteer is processed data, write "based on data from Statistics Sweden (SCB), CC0", not "Källa: SCB". Quality note: the data stops at 2020-12-31.

### 4b Skatteverket test personnummer and samordningsnummer: CC0-1.0, test environments only
- Catalogue `https://skatteverket.entryscape.net/store/9/metadata/147` (Testpersonnummer) and `/153` (Testsamordningsnummer): publisher Skatteverket, access PUBLIC; description: "Testpersonnummer med sekelsiffror för åren 1890-2025. Nya testnummer för kommande år läggs ut i december varje år. **Testpersonnummer får bara användas i testmiljöer.**" (the samordningsnummer description has the same sentence).
- Distributions `/150` (CSV), `/163` (Excel), `/229` (API `rowstore/dataset/b4de7df7-63c0-4e7e-bb59-1f156a591763`), `/155`, `/157`, `/165`: `license = http://creativecommons.org/publicdomain/zero/1.0/`.
- (a) OK for fixtures, unit and integration tests, CI and the test host. Do not load them into production configuration (for example as an allow- or deny-list). (b) OK. Attribution not required; cite the dataset in the fixtures' README.

## 5. Google robotstxt test corpus: Apache-2.0
- `https://api.github.com/repos/google/robotstxt`: `license.spdx_id = Apache-2.0`, not archived; `master` at `22b355ff855419e6a3ff8ff09c0ad7fdb17116f9` (2026-04-01). `robots_test.cc` lines 1-13: "Copyright 2019 Google LLC / Licensed under the Apache License, Version 2.0". No NOTICE file in the repo.
- Not vendored yet (`git grep robotstxt -- src tests` finds nothing).
- (a) OK. (b) and also in our own repo: when we port test cases, keep the Google copyright and Apache-2.0 header in the ported test file (or a `THIRD-PARTY` note beside it), state that the cases were modified/translated (Apache-2.0 §4(b)), and pin the commit.

## 6. `sv_SE` Hunspell word list: LGPL-3.0-only

- In the repo: `src/Ume.Rag.Retrieval/Text/sv_SE/{sv_SE.aff,sv_SE.dic,LICENSE_en_US.txt,LICENSE_sv_SE.txt,README.md}`; embedded by `src/Ume.Rag.Retrieval/Ume.Rag.Retrieval.csproj:8-9` (`EmbeddedResource`) and loaded by `SwedishDecompounder.cs:159-166` through `GetManifestResourceStream`. Our own parser (`HunspellWordList.cs`), no Hunspell library (so no MPL/LGPL/GPL Hunspell code).
- Source: `LibreOffice/dictionaries@25d7793000bc9a149de365e170b67fe255769000/sv_SE/`. `description.xml`: version 2.42, "Den stora svenska ordlistan", publisher dsso.se. `LICENSE_en_US.txt`: "Copyright © 2003-19 Göran Andersson ... made available subject to the terms of GNU Lesser General Public License Version 3." No "or any later version", so **LGPL-3.0-only**. It is **not** the LGPL/GPL/MPL tri-licence that some other Hunspell dictionaries use. SHA-256 of `sv_SE.dic` at that commit `384a2126...66e8` equals the repo copy.
- Which artefacts carry it: `Ume.Rag.Retrieval.dll`, referenced by `Ume.Rag.Worker`, `Ume.Rag.Answering` (and through it the API images). The two LICENSE files are **not** embedded and not copied into any image.

What LGPL-3.0 means here:
- (a) Internal use: no obligations (LGPL duties apply only when conveying). Storing decompounded parts in `Chunks.SearchText` is program output and not covered.
- (b) Distributing a .NET image or bundle to another municipality conveys the Library (the word list). It is unmodified. Under LGPL-3 §4 (Combined Works) we must: give prominent notice that the Library is used and is under LGPL-3; ship the GPL-3 and LGPL-3 texts; ship the copyright notice; and either (§4 d1) use "a suitable shared library mechanism", meaning a copy the user can replace with a modified version, or (§4 d0) provide the Minimal Corresponding Source of our application so the user can relink. A word list compiled into our assembly as a manifest resource fails d1, and d0 means handing over our source. Many would argue that a data file read at run time is mere aggregation, but embedding it makes that argument weaker, not stronger.
- The clean option, cheap to do now: ship `sv_SE.aff`/`sv_SE.dic` as **separate content files** (copied next to the assembly or into `/app/dictionaries/sv_SE/`) with a configurable path, keep the embedded copy out, and ship both LICENSE files and the LGPL-3/GPL-3 texts beside them. Then it is a separately replaceable, unmodified LGPL component, and redistribution needs only the notice, the texts and a pointer to the source (the LibreOffice commit). Because the SHA-256 is pinned, a replaced list is detectable, and `reindex-fts` already exists for a changed list.
- Verdict: OK with obligations. Not a blocker for the pilot. Change it before the first distribution.

## 7. Everything else in the parser image

### 7a Python packages (`src/parser/uv.lock`, 113 entries; 85 installed in the runtime image)
Method: PyPI JSON for every locked version (`License-Expression`, `License`, classifiers), the `dist-info` licence files of the image's `/opt/venv`, and the wheels of the packages with native code (opencv-python-headless, docling-parse, pypdfium2, numpy, scipy, pillow, torch, torchvision, rtree, lxml, pandas) read with `unzip -l`/`unzip -p`.

- No GPL, AGPL, LGPL (as the package's own licence), SSPL, non-commercial or "no production" licence anywhere in the set.
- Weak copyleft: `certifi` 2026.10.19 MPL-2.0, `tqdm` 4.70.1 MPL-2.0 AND MIT. Unmodified: on distribution say where their source is (PyPI/GitHub). OK.
- Notable permissive ones: torch 2.14.1+cpu `Apache-2.0 AND Apache-2.0 WITH LLVM-exception AND BSD-2-Clause AND BSD-3-Clause AND BSL-1.0 AND MIT`; transformers 5.20.2 Apache-2.0; numpy `BSD-3-Clause AND 0BSD AND MIT AND Zlib AND CC0-1.0`; pillow MIT-CMU; regex `Apache-2.0 AND CNRI-Python`; opencv Apache-2.0; scipy BSD-3-Clause (+ Qhull licence); python-dateutil Apache-2.0 / BSD dual; defusedxml PSF-2.0; typing-extensions PSF-2.0.
- Dev group (test image only): pytest MIT, ruff MIT, httpx2 BSD-3-Clause, pip-audit / cyclonedx-python-lib / license-expression / py-serializable Apache-2.0, boolean-py BSD-2-Clause, pip-requirements-parser MIT. OK.
- `pywin32` is in the lock but only for Windows markers; not in the image. `olefile` (named in `../to-verify.md` #109) is not in the current lock.
- `jsonschema`'s `format-nongpl` extra is not installed; its GPL-flagged optional dependencies are absent.

### 7b Copyleft binaries bundled inside permissive wheels
| Wheel | Bundled | Licence | How linked | Obligation on distribution |
|---|---|---|---|---|
| opencv-python-headless 4.14.0.94 | FFmpeg `libavcodec/libavformat/libavutil/libswresample/libswscale` | LGPL-2.1 (`cv2/LICENSE-3RD-PARTY.txt:243-250`) | shared libs in `opencv_python_headless.libs/` | LGPL text (shipped in the wheel), FFmpeg source pointer |
| opencv-python-headless | libgfortran, libquadmath, OpenBLAS, libvpx, libaom, dav1d, libavif, libpng, libdrm, OpenSSL 1.1.1k `libcrypto/libssl` | GPL-3.0-or-later WITH GCC-exception-3.1, LGPL-2.1+, BSD, OpenSSL/SSLeay | shared | notices (shipped); OpenSSL advertising-style acknowledgement |
| numpy 2.5.4, scipy 1.18.1 | libgfortran, libquadmath, scipy-openblas | GPL-3.0-or-later WITH GCC-exception-3.1; LGPL-2.1-or-later (libquadmath, numpy `LICENSE.txt:915-919`) | shared | notices (shipped); gcc source pointer for libquadmath |
| torch 2.14.1+cpu | `torch/lib/libgomp.so.1` (GNU libgomp from GCC 8.5, AlmaLinux 8 build, confirmed with `strings`) | GPL-3.0-or-later WITH GCC-exception-3.1 | shared | none beyond notice; the runtime exception covers it |
| lxml 6.1.3 | libiconv (with zlib, libxml2, libxslt) | LGPL-2.1 (`lxml-6.1.3.dist-info/licenses/LICENSES.txt:40-49`) | **statically** linked into `etree.so` | LGPL-2.1 §6: since lxml's source is public (BSD-3-Clause), a pointer to lxml + libiconv source at the pinned versions satisfies the relink requirement |
| pillow 13.0.0, torchvision 0.29.1, rtree 1.4.1 | libjpeg, libpng, libtiff, libwebp, freetype, harfbuzz, lcms2, openjp2, zstd, brotli, libxcb, libspatialindex | permissive (FTL, MIT, BSD, IJG, Zlib) | shared | notices (shipped in pillow's LICENSE / SBOM) |

All OK for (a). For (b), OK with obligations: LGPL texts are already inside the wheels; add a source-offer paragraph to the NOTICE.

### 7c Debian packages (159 in the runtime image)
- Read from `/usr/share/doc/*/copyright` of the built image (copied out with `docker cp`, not run). All Debian main. Licences present: the usual GPL-2/GPL-3 tools from the `python:3.12.15-slim-trixie` base (bash, coreutils, grep, sed, tar, dpkg, apt, util-linux, perl-base, ...), LGPL libraries (glibc, glib, pango, cairo LGPL-2.1 OR MPL-1.1, gnutls, nettle, gmp, libidn2, libunistring, fribidi, libthai, libseccomp), `libjbig0` GPL-2.0-or-later (through libtiff6 → leptonica → Tesseract), `libdb5.3` Sleepycat, `fonts-dejavu` Bitstream-Vera/GPL-2+ font exceptions, OpenSSL 3 Apache-2.0.
- No AGPL, no non-free packages (sources are `main` only, `Dockerfile:33-44`).
- (a) OK. (b) Distributing a container image distributes these binaries: GPL-2 §3 / GPL-3 §6 / LGPL need corresponding source. Debian's sources at the same snapshot (`https://snapshot.debian.org/archive/debian/20261022T000000Z`) are the practical answer: record the snapshot and package list with each released image and include a written offer, or ship a source tarball if a recipient asks. The copyright files are already in the image.

---

## Side findings (outside the licence question, but found while checking)

1. **The platform repository has no LICENSE** (`ls LICENSE*` at the repo root: none). Before "distribute images to other municipalities" can happen, the municipality must decide under what terms it gives out its own code (EUPL-1.2 is the usual choice for Swedish public sector open source; compatible with everything above, including LGPL-3). This also decides which LGPL-3 §4 route (d0 or d1) is open for the sv_SE list. Not part of P2, but (b) depends on it.
2. **opencv-python-headless 4.14.0.94 bundles OpenSSL 1.1.1k** (`opencv_python_headless.libs/libcrypto-a324fe28.so.1.1.1k`, `libssl-8248cfc9.so.1.1.1k`), end of life since 2023 and with known CVEs. It is only reachable through FFmpeg's network protocols in `cv2.VideoCapture`, which the parser does not use, and the parser runs without egress. Worth a line for the security review / pip-audit allowlist, not a licence issue.
3. **SCB name statistics end at 2020-12-31** and SCB has stopped producing them (2024). The gazetteer will miss names that became common after 2020; Skatteverket only publishes newborn top lists.
4. `plans/parser/design.md` §9.2 "`tessdata_fast` or `best`, *to verify*" is answered: Debian `tesseract-ocr-swe 1:4.1.0-2` is **tessdata_fast** (Debian copyright `Upstream-Name: tessdata_fast`).
5. Not reviewed (not in the pilot): LibreOffice, its fonts and `python-oxmsg` (P2 formats, per the register row).

## What the platform team must do

1. **Generate `src/parser/LICENSES.md`** (planned in `../platform/repo-and-deployment.md`, Licences row) and copy it into the image at `/usr/share/doc/ume-rag-parser/`:
   - Python: name, version, SPDX and licence file path for every package in `/opt/venv` (from `dist-info`); the LGPL/GCC-exception binaries table of 7b.
   - **docling-parse third-party notices** that its wheel lacks: qpdf (Apache-2.0), FreeType (FTL credit sentence), libjpeg-turbo (IJG sentence + BSD-3-Clause + Zlib), OpenJPEG (BSD-2-Clause), lcms2 (MIT), blend2d/asmjit (Zlib), utf8 (BSL-1.0), nlohmann json and cxxopts (MIT), pybind11 (BSD-3-Clause), PDFium jbig2 (BSD-3-Clause), Adobe cmap resources (BSD-3-Clause).
   - Models: Heron `docling-project/docling-layout-heron@8f39ad3c` Apache-2.0; TableFormer `docling-project/docling-models@fc0f2d45` CDLA-Permissive-2.0; KB NER `KBLab/bert-base-swedish-cased-ner@f3c83af8` MIT ("Copyright (c) National Library of Sweden / KBLab", MIT text), "fine-tuned on SUC 3.0 (Stockholm-Umeå Corpus), Språkbanken, DOI 10.23695/wy84-ar30".
   - Debian: snapshot date, `dpkg-query -W` list, pointer to `/usr/share/doc/*/copyright`, and a written offer for GPL/LGPL sources from snapshot.debian.org.
2. **Ship the model licence texts in `/opt/models`**: add `LICENSE` files (Apache-2.0 for Heron, CDLA-Permissive-2.0 for TableFormer, MIT for KB NER) to the image beside the weights, for example by extending `model_manifest.json` with the model's README at the pinned revision (hash-checked) plus a checked-in licence text where the HF repo has none.
3. **sv_SE word list**: before any distribution, move `sv_SE.aff`/`sv_SE.dic` from `EmbeddedResource` to content files with a configurable path, and ship `LICENSE_en_US.txt`, `LICENSE_sv_SE.txt` and the LGPL-3.0 + GPL-3.0 texts beside them in every .NET image that contains `Ume.Rag.Retrieval.dll` (worker, API/answering). For the internal pilot the current embedding is acceptable; the README's "open in P2" line can be closed as "LGPL-3.0-only; internal use OK; separate file + notices before distribution".
4. **Repository NOTICE** (root `NOTICE` or `THIRD-PARTY-NOTICES.md`), at least:
   ```
   Ume.Rag includes or uses third-party components:
   - Docling, docling-core, docling-parse, docling-ibm-models: MIT, Copyright The Docling Contributors / IBM.
   - Docling layout model "heron": Apache-2.0. TableFormer models: CDLA-Permissive-2.0. (IBM / Docling project)
   - KB BERT NER (KBLab/bert-base-swedish-cased-ner): MIT, National Library of Sweden / KBLab.
     Fine-tuned on SUC 3.0, Språkbanken Text, University of Gothenburg (doi:10.23695/wy84-ar30).
   - PDFium (via pypdfium2): BSD-3-Clause and Apache-2.0, The PDFium Authors; pypdfium2: Apache-2.0 / BSD-3-Clause.
   - Tesseract OCR and tessdata_fast "swe": Apache-2.0, Google / Hewlett-Packard.
   - Portions of this software are copyright © The FreeType Project (www.freetype.org). All rights reserved.
   - This software is based in part on the work of the Independent JPEG Group.
   - FFmpeg (in opencv-python-headless) and libiconv (in lxml): LGPL-2.1; source: ffmpeg.org, gnu.org/software/libiconv.
   - Swedish word list "Den stora svenska ordlistan" 2.42, Copyright © 2003-19 Göran Andersson: LGPL-3.0.
   - Name statistics based on data from Statistics Sweden (SCB), CC0-1.0.
   - Test identity numbers from Skatteverket (CC0-1.0); used in test environments only.
   - Test cases adapted from google/robotstxt, Copyright 2019 Google LLC, Apache-2.0.
   ```
5. **Fixtures**: Skatteverket test numbers only in `tests/`, fixtures and test hosts, with a README stating the source dataset and "får bara användas i testmiljöer". Ported robotstxt cases keep the Google Apache-2.0 header and the source commit.
6. **No replacement is needed** for any component. Optional: track the OpenSSL 1.1.1k inside opencv-python-headless with the security review, and decide the platform's own licence before distribution to other municipalities.
7. Record the results in the stream results file as `../to-verify.md` rows #109-#113 (all answered by this review; #109's `LICENSES.md` remains as work item 1).
