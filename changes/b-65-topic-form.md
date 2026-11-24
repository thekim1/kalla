- **Topic rule preview** (B-65, part 1): `POST /api/collections/{id}/topic-rules/preview` says what one topic rule
  matches among a source's live documents before it is saved ("Matches N of M"), with the first ten, through the same
  matcher the worker tags with; an invalid rule is refused as it would be at Save. The Topics tab's suggestions now
  read an upload's file names exactly as the rules do, so a name written with a leading slash is in no folder section.
