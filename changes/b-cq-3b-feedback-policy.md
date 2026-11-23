- **Feedback is its four roles' in the API too** (#980): the feedback list and its totals (`GET /api/feedback`,
  `GET /api/feedback/summary`) answer platform and department admins, reviewers and viewers, as the Feedback page
  already did (admin design §3.3); the editor, pipeline-editor and skill-editor roles get 403. Home's thumbs and a
  knowledge base's downvotes per document are unchanged.
