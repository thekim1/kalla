- **`init-deployment.sh` keeps a site overlay** (#1101 D7): a re-run used to rebuild `COMPOSE_FILE` from the bundle's
  own overlays and drop a file of the operator's own. It now keeps every other file, after the bundle's.
- **The release dry run upgrades an odd Compose host** (#1101 D7): the last release's bundle with a project, ports and
  host paths of its own is upgraded as the upgrade guide says, and its project, ports, host paths and data must stay.
