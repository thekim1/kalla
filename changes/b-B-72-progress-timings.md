- **Progress and step times in the chat extension** (B-72, #959): two new optional members of the chat API's `kalla`
  extension, `true` or `false` (any other value is 400 `invalid_request`). With `kalla.progress`, a streamed turn that
  gets a model answer starts with one `kalla` chunk `{"progress":"writing"}`, written once the gateway has accepted the
  answer call, so a spent budget or an unusable key is still an HTTP error before the first byte; it carries the related
  documents, without `cited` and `number`, when those are asked for too. A fixed text and a non-streamed answer have
  none. With `kalla.timings`, the answer's `kalla` has `timings`: `condense`, `retrieve`, `first_token` and `total` in
  whole milliseconds, each left out when its step did not run, and a non-streamed answer also gets them in a
  `Server-Timing` header. A request without the members gets the same answer as before, byte for byte.
