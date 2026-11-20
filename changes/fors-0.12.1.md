- **The Portainer stack runs Fors 0.12.1** (`deploy/portainer/compose.portainer.yaml`, the gateway's images pinned by
  digest). 0.12.1 keeps the gateway's Data Protection names from before the rename, so a gateway upgraded from 0.11
  still reads its provider credentials and the sign-in client secret saved in its setup wizard (thekim1/fors#116,
  #117). The gateway contract is unchanged.
