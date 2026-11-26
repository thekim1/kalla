- **`upgrade-to-0.9.sh` reads the deployment** (#1101 D2, D3): the Compose upgrade from before 0.9.0 runs on a shared
  library, `deploy/upgrade/lib.sh`.
  - It finds the old project by its containers' labels and each volume's data from `docker inspect`.
  - It prints its plan and changes nothing without `--apply`, which replaces `--yes`.
  - Data on a host path is pointed to, never copied, and a missing or empty source is refused.
  - It no longer renames the database roles: `kalla-roles` does that when the release starts, as in the Portainer
    stack.
