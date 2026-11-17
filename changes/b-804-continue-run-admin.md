- **Continue a crawl run** (#804): a crawl run that failed, was cancelled, stopped at the page limit or left failed
  pages can be continued from its run page or, for the latest run, beside *Sync now* on the source. The new run fetches
  the pages the run left open or failed and follows their links, instead of starting again from the start addresses.
  It checks those pages against the current scope and depth limit and proposes no *not seen* deletions. Its run page
  says what it took over and what it left out.
