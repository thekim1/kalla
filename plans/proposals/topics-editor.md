# Topics that are easy to set (2026-11-21)

The question (user, 2026-11-21, after finding the Topics field): **topics are bad to use. They expect JSON, sit in a
place where nobody looks, and say nothing about how to set them up. How do we make them easy?** The user's own
preference: **an editor should just pick topics from the ones found**, as in step 4 below. This is a proposal for the
main session, checked against the designs (processing §7, admin §5.5.2 and §6.6, engine §4) and the code on `main`. It
becomes binding only through a Decisions entry and the designs for its areas.

## Where it stands

- A topic is a tag that a **path rule** puts on a document's chunks (processing §7). The rules are the template
  parameter `pathRules` of the source's **processing** (pipeline). A change is published through the publication plan
  and re-chunks the documents it touches (`chunk` stage, engine §4).
- The only editor is the generic parameter form on the **source page** (*Processing* › *Edit processing* ›
  *Topics*): a raw JSON textarea. The knowledge base's **Topics tab** lists the tags with document counts and, for the
  owner, the rules that set them, but can't add any.
- Step 1 of #929 is in two pull requests: #930 (a pasted address works as `match`, a trailing `/` covers the pages under
  it) and #931 (the Topics tab links the sources and shows an example; the field's hint is an example). They make
  today's editor usable. They don't make it easy.

## Proposed

### 1. Topics are set on the Topics tab

The knowledge base's Topics tab becomes the place to add, change and remove topics, for whoever has `EditPipelines` on
the collection (the role that edits processing today). The source page's *Topics* field becomes a read-only list with a
link to the tab. Nothing moves in storage: the tab writes the same `pathRules` of each source's processing, through the
same publication plan, with the same copy-on-write for a department on a platform processing (admin §6.6).

### 2. Pick from the topics found (the user's preference)

For each source, the tab offers the **sections the source already holds** as candidate topics, each with its page
count (the numbers below are made up):

```
Suggested from umea.se                                   Pages
[ ] Bygga, bo och miljö        /bygga-bo-och-miljo/        612
[ ] Utbildning och barnomsorg  /utbildning-och-barnomsorg/ 488
[ ] Kommun och politik         /kommun-och-politik/        401
    › show the sections under it
                                    [Add 2 topics]
```

- **Website sources:** the first one or two path segments of the live documents' `Documents.Url`, grouped and counted,
  with sections of fewer than 5 pages left out. The name comes from the section page's title where one is crawled (the
  document whose path is the section itself), else from the segment (`bygga-bo-och-miljo` → *Bygga bo och miljo*).
- **Upload sources:** the folders of the uploads' display names (`HR/`, `HR/Policys/`), counted the same way.
- **Ticking one** proposes the topic name (the segment, lower case, which the tag format already allows), editable
  before saving, and the rule (`{ "match": "/bygga-bo-och-miljo/", "on": "path", "tags": ["bygga-bo-och-miljo"] }`).
- Sections already covered by a rule are shown ticked, with their topic.
- No model call: it is a grouping query over rows the database already has. It is read through one new endpoint (see
  *API*).

### 3. A form for the rest, instead of JSON

For a topic the suggestions don't cover, *Add topic* opens a form:

```
Topic       [bygglov                      ]
Pages       (•) under the address  [https://www.umea.se/bygga-bo-och-miljo/bygglov/ ]
            ( ) whose title contains [            ]
            ( ) of the file type     [PDF ▾]
Source      [umea.se ▾]                  (only with more than one source)
            › Advanced: a regular expression on the path, a document type, a date from the path
```

The form writes the same rule shapes as today (`path`, `title-regex`, `mediaType`, `path-regex`). Advanced keeps
everything the JSON could express, so the source page's JSON field can go.

### 4. What a rule will match, before saving

Under the address or pattern: **"Matches 212 of 2 323 pages"**, with the first ten listed. A rule that matches 0 pages
gets a warning before saving ("No page of umea.se is under this address"), which is the trap #929 found. It is the same
grouping endpoint, asked for one rule.

### 5. Saving says what happens

*Save* shows the plan's effect on the tab itself: "Saving processes 212 pages again; the topics appear when that is done
(about N minutes)". The publication runs as today. Until it is done the new topics are listed as *pending*, from the
plan's draft, so the editor sees that the save was taken.

## API

- `GET /api/collections/{id}/topic-suggestions?sourceId=&depth=1|2`: per source, the sections (website) or folders
  (upload) of the live documents with their counts, the title of the section page where there is one, and the topic
  already set for each, if any. Read, for the owning department only (the rules are the sources' settings, admin §3.2).
- `POST /api/collections/{id}/topic-rules/preview` with one rule and a `sourceId`: `{ matched, total, sample[≤10] }`.
  Edit.
- Saving uses the existing publication plan (`POST /api/pipelines/{id}/publications`) with the new `pathRules`. A knowledge base
  with several sources on different processings gets one plan per processing, shown as one save.

## Order

1. #930 and #931 (step 1, merged together): usable today.
2. **Pick from the topics found** (§2) with the suggestions endpoint, on the Topics tab. This is the user's preference and
   covers most of it on its own.
3. The form and the match preview (§3, §4).
4. *Pending* topics and the save's effect on the tab (§5); the source page's JSON field becomes read-only.

## For the main session to settle

1. Who edits topics: `EditPipelines` as today (topics are processing), or `Edit` on the collection, since a topic is a
   property of the knowledge base to an editor? The second needs the plan endpoint to accept a collection editor for
   `pathRules` only.
2. The suggestions' depth: the first path segment only, or the first two? The first level of a municipal site is a
   handful of broad sections, which may be too coarse for an assistant; the second can run to many more. To be counted
   on the pilot's crawl before choosing.
3. Should a suggested topic's name be the section page's title in lower case with hyphens (`bygga-bo-och-miljo`), or
   something shorter that the editor types? The tag format allows either.
4. A knowledge base with several sources: one topic across sources (the same tag from rules on each), or a topic per
   source? Today's model already allows the first: the tab would group by tag.
