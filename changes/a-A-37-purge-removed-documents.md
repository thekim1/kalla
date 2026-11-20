- **Documents a source no longer has are removed for good** (#878): a document removed from a source that still exists
  was soft-deleted but never purged. The nightly maintenance now deletes it with its chunks 14 days after the removal,
  unless a later run found it again in the meantime, and its stored raw copy goes after the blob purge delay unless
  another document has the same content.
