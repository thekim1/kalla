- **`upgrade-to-0.9.sh` plans before it acts** (#1101 D2, D3; Compose hosts upgrading from 0.8 or earlier): run it
  without options to see the plan, then with `--apply`. `--yes` is gone. Data on a host disk is no longer copied into
  empty named volumes: the new volumes point at the same paths (*0.9.0*, *Compose*).
