- **Deleted sources and knowledge bases leave the database** (#878): the nightly retention removes a deleted
  source's row once its documents are purged (14 days after the delete) and its upload blobs are purged (the blob
  purge delay), and a deleted knowledge base's row, with its shares, once its last source's row is gone.
