# Guldset, utkast: Kommun och politik (umea.se)

Draft for DoIT review. File: `draft.csv` (UTF-8 with BOM, comma-separated, RFC 4180 quoting, CRLF line endings).
Columns: `nr,typ,fråga,förväntat_svar,dokument_id,dokument_titel,dokument_url,kommentar`.
Source: `pages.jsonl` (1,166 pages, first ~1,800 characters of each page), an export of a local crawl of umea.se's
"Kommun och politik" slice on 2026-11-01; it is not in the repository. Written 2026-11-01. The same questions are in
`Turkos-guldstandard-utkast.xlsx` in this folder.

**Open question for DoIT:** the draft covers only this 1,166-page slice. Whether the set is extended to the full
umea.se crawl before it is entered is DoIT's call at review.

## Counts

| typ | antal |
|---|---|
| besvarbar | 138 |
| tidsberoende | 21 |
| obesvarbar | 40 |
| **total** | **199** |

- 159 distinct pages are targets. No page is the target of more than 3 questions.
- 14 answerable questions list 2–3 target pages: identical duplicate pages (e.g. *Skattesatser* 1/404,
  *Intäkter och kostnader* 0/403, *Planering över året* 2/383, the ~15 identical *Föredragningslistor, handlingar och
  protokoll* pages), or two pages that answer the same thing equally well. Of these, 11 are besvarbar and 3
  tidsberoende.
- Against the decision's range (Decisions "Gold set drafted by the main session, reviewed by DoIT": about 120–150
  answerable, 30–50 unanswerable, at least 10 temporal): `scripts/grading/grading.py` counts every question with
  expected documents as answerable, so the 138 besvarbar and 21 tidsberoende rows count as 159 answerable, above the
  range; the 40 obesvarbar rows count as off-topic.
- Rows are sorted by type, then by topic. Reviewers may want to shuffle before use.

## Topic distribution (besvarbar + tidsberoende)

| Ämne | besvarbar | tidsberoende |
|---|---|---|
| Politik, möten och fullmäktige | 14 | 6 |
| Förtroendevalda (uppdrag, arvode, säkerhet) | 9 | 1 |
| Nämnder, utskott och råd | 14 | 1 |
| Budget, ekonomi och skatt | 9 | 3 |
| Statistik och befolkning | 4 | 2 |
| Mål, styrdokument och hållbarhet | 8 | 0 |
| Val | 5 | 3 |
| Organisation, förvaltningar och kontakt | 8 | 1 |
| Kommunala bolag | 2 | 0 |
| Dialog, synpunkter och felanmälan | 6 | 0 |
| Allmänna handlingar, arkiv och sekretess | 13 | 1 |
| Överklaga beslut och anslag | 3 | 1 |
| Lokala föreskrifter och ordning | 4 | 0 |
| Internationellt arbete och EU | 8 | 0 |
| Nationella minoriteter och samiska | 4 | 0 |
| Kommunikation och grafisk profil | 8 | 0 |
| Historia | 6 | 0 |
| Visselblåsning | 2 | 1 |
| Skola, social omsorg och arbetsmarknad | 7 | 0 |
| Övrigt (nyheter, utmärkelser) | 4 | 1 |

The topic label is assigned from the first target page. It is not a column in the CSV.

Unanswerable categories: (a) municipal topic not in the index: 10. (b) regional, state or other non-municipal: 10.
(c) plausible but non-existent specific: 14. (d) personal data about private individuals: 6.

## How the questions were made

- Pages were read in batches spread over the whole URL-sorted file (index 0–1165): politics and meetings, committee
  pages, finance, statistics, elections, archives and public records, rules, international work, minorities,
  graphic profile, history and the news/notice pages at the end.
- Questions are written as a resident, journalist, politician or employee would ask them. They are a mix of keyword
  style (`deadlin interpelation fullmäktige`), full questions, informal wording and typos (`gatlampan e trasig vem
  ringer ja`, `vem blev hedersmedborare 2023`, `fyrverkier`), a few long multi-part questions, and one in English.
- Each answer is taken from the page text and kept to 300 characters or fewer. A script checked that every number in
  every answer occurs in the target page text. The only mismatches are formatting differences in the source (for
  example "mellan 1 och 14" vs "1–14", a non-breaking hyphen in a phone number, or a date that is in the page title).
- Phrase overlap: a script found the longest run of consecutive words shared by each question and its target page.
  Every question with a run of 4 or more words was reworded. No remaining question shares 4 or more consecutive words
  with its page.

## How the unanswerable questions were checked

Each unanswerable question carries the regexes that were searched (case-insensitive, over title + text of all
1,166 pages) in `kommentar`. Every hit was read by hand. Where a regex matched, the match was a false positive, and
the comment says so:
- `Norrlands universitetssjukhus` only appears in the alcohol-ban area description (379);
- `tåg … Vännäs` only appears in a 1957 migration story (831);
- `landshövding` only appears in historical stories;
- `NUS` as a bare regex matches "Magnus" and "minus", so it was replaced.

Candidates that the search showed the index **does** answer were dropped as unanswerable:
- snow clearing (634, 637);
- building permits in general (624, 764);
- school leave (433);
- borgerlig vigsel (825).

Some unanswerable questions sit next to real pages on purpose:
- spårvagn: an interpellation exists (814), but there is no decision and no cost;
- ungdomsnämnd: a motion for an ungdomsfullmäktige exists (435), but there is no committee;
- Reykjavik as a twin town: Umeå has five twin towns, none in Iceland;
- motion 58/2025: the 2025 list ends at 47/2025;
- medborgarförslag: page 780 is related but does not answer. The reviewer should decide.

## Time-dependent questions (tidsberoende)

The 21 rows cover the following. `kommentar` says what changes over time.
- tax rate 2026;
- revenue 2025;
- population at the turn of the year 2025/26;
- housing completed;
- the next council meetings, judged from 2026-11-01;
- the latest motion;
- election results 2026;
- election and early-voting dates;
- start of the new term of office;
- current office holders: kommunstyrelsens ordförande, stadsdirektör, revisionens ordförande, the interim head of
  the whistleblowing function, the governing parties;
- reception and overförmyndare opening hours;
- grundarvode 2026;
- the interim result for the year;
- Superkommun ranking;
- notice and appeal deadlines (2026-11-17, 2026-11-20).

Several of these go stale within weeks:
- the council protocol notice is taken down 2026-11-20;
- the appeal deadline for Gökungen is 2026-11-17;
- new committees are appointed 2026-11-30;
- the 2027 term of office starts 1 January 2027.

Several dates above (2026-11-17, 2026-11-20) have passed. DoIT's review re-dates or replaces these rows when the set is
entered.

## Known weaknesses

1. **Bias from writing questions from the pages.** Every answerable question was written after reading its target
   page. The questions are paraphrased, but their topics and granularity still follow what the pages say. Real user
   questions will be vaguer and will more often span several pages. DoIT should add questions from real sources
   (switchboard/contact-centre logs, the *Kommunen svarar* page, journalist requests) before thresholds are set.
2. **Only the first ~1,800 characters per page were visible.** Answers come from that excerpt. A page can contain more
   (or contradicting) text further down. For the same reason an "unanswerable" question could be answered deeper in
   a long page. The grep only covered the excerpt.
3. **The index is broader than "Kommun och politik".** It includes school, social care, leisure facility service
   levels, Finnish/Sami/Meänkieli pages, building-permit notices and news. A few answerable questions (school leave,
   school placement, preschool staffing, e-scooters) sit at the edge of the section. Questions about those topics
   that are not answered in the index were put under obesvarbar (a). The reviewer should decide whether the edge
   topics belong in this set.
4. **Single-target bias.** Most rows list one page, but near-duplicates exist. Committee pages repeat chairs and
   contacts, news items repeat facts from fact pages (whistleblowing: 269, 857, 869, 887, 897, 909; AI: 84, 941, 969,
   1090; climate: 480, 517, 865, 452). Where this was noticed it is in `kommentar`. A retrieval that returns a sibling
   page may be counted as a miss unfairly.
5. **Named office holders.** Some answers name elected officials or senior officials in their public role (allowed),
   and these go stale after the 2026 election. They are marked tidsberoende, or noted, where the answer is the
   person.
6. **Known inconsistency in the source.** The climate-neutral year is 2040 in the municipal goal (480/517) but 2030
   in the EU Mission text (452/865). The question uses 480/517, and the comment notes the conflict.
7. **Category (d) uses invented private names** (Anna Svensson, Erik Johansson, Lisa Berg) checked to be absent from
   the index. These test refusal, not retrieval. DoIT may want them in a separate safety set.
8. **No Finnish, Sami or Meänkieli questions.** The index contains many pages in those languages. Only one question
   is in English.
9. **Topic labels are rough** and not part of the CSV.

## Reproducing the check

The validation script (`tools/check.py`) and its source `pages.jsonl` were in the drafting session's scratchpad and are
not in the repository, so the check cannot be re-run from here. It checked:
- the BOM and the header;
- that every `dokument_id` exists in `pages.jsonl`, and that its title and URL match position by position;
- that obesvarbar rows have empty answer and document columns;
- the 300-character answer limit;
- that the counts are within range;
- that there are no duplicate questions;
- that `nr` runs in sequence.

## From the draft to `human` questions

The draft does not load as it is:

- `scripts/grading/grading.py` reads `question` (or `text`) and `expected_documents` (or `expectedDocuments`,
  `expectedDocumentIds`), with several documents split on `|` or a newline. The draft's columns are `fråga`,
  `förväntat_svar`, `dokument_id` and `dokument_url`, with `;` between several targets.
- The admin API takes one question per call (`POST /api/collections/{id}/evaluation/questions`, with `question`,
  `expectedAnswer` and `expectedDocumentIds`, the collection's live document ids).

Entry is stream b's B-L2 ("Gold set in, thresholds out"), after DoIT has approved the set. Targets are mapped by
`dokument_url`, not `dokument_id`: the ids come from the local crawl of 2026-11-01 and do not exist in the pilot crawl,
so each URL is looked up among the pilot collection's documents and a URL with no live document is reported, not
dropped. The main session records DoIT's approval (who approved, the date, the file's version) in
[`../work/results-main.md`](../work/results-main.md) before B-L2 enters the set.
