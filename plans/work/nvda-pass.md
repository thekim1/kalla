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

## The journey (§5.4's steps)

| # | Do | Check especially |
|---|---|---|
| 1 | Sign in; land on Home | the empty state and **Create knowledge base** are read |
| 2 | Create a knowledge base in the drawer | the information-class choice: each option and its explanation are read |
| 3 | Add a website source (type cards) | the type cards are reachable and named; the address field's hint |
| 4 | Run the check | the check's results are read, including a warning |
| 5 | Preview two pages | *Before* and *After* are two named regions; the stage strip's states are words |
| 6 | Save and fetch; follow the run | progress is spoken as it changes; *failed only* filter; a document's reason |
| 7 | Test questions: add one of your own | the expected-document picker (search, choose, remove) |
| 8 | Publish the knowledge base | the evaluation report is read as text before the button |
| 9 | Create an assistant | the template field; the collapsed *Advanced* section opens and is announced |
| 10 | Test tab: ask a question | "The answer is ready" is spoken once; sources and coverage can be read |
| 11 | Publish the assistant | a refusal (V-rule) is read with its reason |
| 12 | Audience: add Open WebUI | a residency refusal's sentence is read |
| 13 | Follow **Open in** | the link says where it goes |

Then, briefly: the **Feedback** page, the **Topics** tab, the **Map** table and **Settings**, with the checks above.

## Recording

For each problem: the page, the step, what NVDA said, and what it should have said, ideally with NVDA's speech viewer
text (*Tools › Speech viewer*). In `results-main.md` under row 96: the date, the NVDA and Firefox versions, the stack's
release, who ran it, and the issues opened (or "none").
