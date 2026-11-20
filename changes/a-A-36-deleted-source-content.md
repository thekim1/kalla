- **A deleted source's documents are removed** (#878): the worker's new command `delete-source` cancels the source's
  running run, soft-deletes its documents and their chunks, and deletes its upload files, whose stored copies go after
  the blob purge delay. The nightly maintenance then removes the documents for good after 14 days. Searching already
  left a deleted source out.
