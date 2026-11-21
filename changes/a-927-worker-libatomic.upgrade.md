- **Sources that render JavaScript** (#927): on 0.9.0 and 0.9.1 their pages failed with `render.unavailable` and went
  back to the queue. Nothing to do but update: the queued fetch jobs run once the new worker is up. A source whose fetch
  mode was set to plain HTTP as a workaround can be set back to the browser mode.
