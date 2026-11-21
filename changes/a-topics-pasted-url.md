- **Topic rules take a pasted address** (#929): a `path` rule on a website source may be written as the address from
  the browser (`https://www.umea.se/bygga-bo-och-miljo/bygglov/`) or with the host first (`www.umea.se/…/**`). The host
  must be the page's, and the query and fragment are dropped. Before this, such a rule was compared with the path alone
  and tagged nothing. A rule that ends in `/` now also covers every page under it, for uploads too (`HR/`).
