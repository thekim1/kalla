- **The assistant page's five tabs** (B-78 part 1, admin §5.5.4): *Build*, *Quality*, *Versions*, *Access* and *Usage*
  replace the seven tabs. *Build* has the draft and, beside it from 1,280 px, the test panel, sticky; below that the
  panel folds under the form as *Try the draft*, and on a phone *Try the draft* in the save bar opens it as a sheet.
  *Quality* holds the test questions, the answer evaluation and the content gaps. *Access* tells one story with one save
  bar: who can use it, the apps, the storage of conversations, the model name, and *Check access* folded at the end.
  *Usage* shows the last use and a period's questions, not found and ratings above the Cost card. Old `?tab=` values
  open the tab their content moved to; the app's own links use the new names (`build`, `quality`, `versions`, `access`,
  `usage`).
- **`GET /api/feedback/summary`** returns `lastUsedOn`, the last day with an answer, whatever the period.
