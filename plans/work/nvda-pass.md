# NVDA pass of the admin UI (to-verify row 96)

The last open part of the admin UI's accessibility check (`admin/design.md` §10.1: "one NVDA + Firefox pass of the
pilot journey"). The automated checks (axe in three themes, target size, reflow, the keyboard-only pilot journey) run
in CI. This pass is the one thing they can't show: what a screen-reader user hears. Your team runs it on a Windows PC
(user, 2026-11-22). Report what you find as GitHub issues labelled `stream-b`, one per problem. When the pass is done,
record it in `results-main.md` under row 96.

## Setup

- Windows 10 or 11, the current **NVDA** (nvaccess.org) and the current **Firefox**, both with default settings.
- A Källa stack with the demo seed: the test host, or a local FakeGateway stack (`README.md`). Sign in as a department
  admin (`rag-platform-admin` locally) and use only the keyboard.
- The useful NVDA keys: `H` / `Shift+H` next / previous heading, `D` landmark, `F` form field, `B` button, `T` table,
  `Insert+F7` the list of links, headings and landmarks, `Insert+Space` to switch between browse and focus mode,
  `Ctrl` to stop speech.

## What to listen for on every page

1. **Page change:** after a link in the menu, NVDA reads the page's heading (focus moves to it).
2. **Skip link:** the first `Tab` on a page offers *Skip to content*, and it works.
3. **Landmarks and headings:** `Insert+F7` lists a main region, the navigation and headings that outline the page.
4. **Buttons and links say what they do** ("Publish", "Fetch now", not "button" or "click here"). An icon-only button
   has a name.
5. **Form fields:** each field reads its label, any hint, and whether it is required. After a failed save, focus lands
   on the first wrong field and NVDA reads its error.
6. **Status changes are spoken once:** a toast, "Saved", a job's progress as states change (not every second), the
   test panel's "The answer is ready".
7. **Tables:** NVDA reads the caption, the column headers as you move across cells, and the sort order of a sorted column.
8. **Dialogs and drawers:** focus moves into them and NVDA reads their title. `Esc` closes them, and focus returns to
   the button that opened them.
9. **No information by colour alone:** a state (failed, published, flagged) is also said in words.
10. **Tabs:** arrow keys move between tabs, and NVDA reads each tab's name and "selected". A tab with a dot says so in
    its name ("Quality, needs attention"); a tab without one doesn't.
11. **The save bar:** after the first change on a form, NVDA says once that there are unsaved changes, and *Save* is
    reachable. A focused field is never hidden behind the bar (also on a narrow window). Leaving the page or the tab
    with unsaved changes opens a dialog that is read; *Discard* asks once.
12. **Menus:** a row's or a header's ⋯ button is named ("More actions for …"); its menu opens with `Enter`, arrow keys
    move through the items, `Esc` closes it and focus returns to the ⋯ button.

## The journey (§5.4's steps)

| # | Do | Check especially |
|---|---|---|
| 1 | Sign in; land on Home | the greeting and the empty state; **Create knowledge base** is read |
| 2 | Create a knowledge base in the drawer | the order: name, what goes in, who may read it, department and contact; each reading level and its explanation are read, none preselected; the folded line "Contact: … · Change" and its button "Change the contact" |
| 3 | Add a website source (type cards) | the type cards are reachable and named; only that type's fields follow; *More options* says whether it is open, and opens by itself on an error in it |
| 4 | Run the check | the check's results are read, including a warning |
| 5 | Preview two pages | *Before* and *After* are two named regions; the stage strip's states are words |
| 6 | Save and fetch; follow the run | progress is spoken as it changes; *failed only* filter; a document's reason |
| 7 | The knowledge base page | its six tabs (*Overview*, *Content*, *Documents*, *Topics*, *Quality*, *Sharing*) and their counts; the Overview's four tiles (*Documents*, *Last fetch*, *Quality*, *Used by*), and a tile that needs action says so in words |
| 8 | *Quality* tab: add a test question of your own | the expected-document picker (search, choose, remove); then *Tune the search*: *Try without saving* says the run started, and the run is read when it shows |
| 9 | Publish the knowledge base | the evaluation report is read as text before the button; the Publish dialog's title, its warnings and the test run's cost |
| 10 | Create an assistant | the *Start from* cards are a group with a name, and a refused template's error is read; the collapsed *Advanced* section opens and is announced |
| 11 | *Build* tab: the knowledge rows and general knowledge | a row's knowledge base, department and topics; *Limit to topics* opens a field that reads its label; *General knowledge* is a radio group of four, each level's description read |
| 12 | *Build* tab: ask a question in the test panel | at full width beside the draft; in a narrower window as *Try the draft* under the form; at phone width from *Try the draft* in the save bar, as a full-height sheet that is read as a dialog. "The answer is ready" is spoken once; sources and coverage can be read |
| 13 | Publish the assistant | the Publish dialog (the diff, the warnings, the cost); a refusal (V-rule) is read with its reason |
| 14 | *Access* tab: add Open WebUI | one story top-down (who can use it, the apps, storage, the model name, *Check access* folded at the end) with one save bar; a residency refusal's sentence is read |
| 15 | Follow **Open in** | the link says where it goes |
| 16 | *Quality* and *Usage* tabs | test questions and the latest report on *Quality*; on *Usage* the last use, questions, not found and ratings, and the *Cost* card's tables |

Then, briefly, with the checks above:
- the **Feedback** and **Costs** pages: each filter chip reads its name and value ("Rating:, Thumbs down"); a set chip's
  × is named ("Remove the rating filter") and focus stays on the chip after it; *Clear filters*; the count is spoken
  once after a change; *Custom…* adds two labelled date fields;
- the **Audit log** (chips and *More filters*) and the **Map** table;
- the knowledge base's **Topics** tab;
- **Settings**: a section's save bar and the leave guard;
- the side menu: its groups are named, and the current page is marked.

## Recording

For each problem: the page, the step, what NVDA said, and what it should have said, ideally with NVDA's speech viewer
text (*Tools › Speech viewer*). In `results-main.md` under row 96: the date, the NVDA and Firefox versions, the stack's
release, who ran it, and the issues opened (or "none").
