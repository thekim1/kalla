- **Faster CI** (#1084): a release builds its four Dockerfile images at the same time, beside the admin UI and the .NET
  publishes, and runs the bundle's smoke test and the Portainer boot side by side while the oldest supported release's
  images are pulled for the upgrade test. `verify.yml` folds the `ownership` and `doc-refs` guards into `changes`, runs
  the evaluation smoke inside `backend` on its build, runs the admin UI's lint, typecheck and unit tests at once, and
  leaves the worker image's driver check to the release's build job.
