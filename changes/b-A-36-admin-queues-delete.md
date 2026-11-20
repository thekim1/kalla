- **Deleting a source in the admin UI takes its content out at once** (#878): the delete queues the worker's
  `delete-source` command in the same change.
