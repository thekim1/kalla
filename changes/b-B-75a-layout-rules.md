- **Admin UI layout rules, first part** (B-75, admin §5.12): number fields are short and page forms are at most 680 px
  wide, with their selects about 380 px; zeros in lists and counts are grey; a chosen radio card has the accent border
  and soft background; *Not scanned for malware* is said once in the file list's summary ("13 files were not scanned
  for malware: the scan was off when they were stored") instead of on each row, and
  `GET /api/sources/{id}/files/summary` returns `notScanned`; the source and run pages keep their section titles inside
  the cards; detail pages have a breadcrumb (*Knowledge bases / …*), which replaces the run page's *Back to the
  source*, and the menu item stays lit on them.
