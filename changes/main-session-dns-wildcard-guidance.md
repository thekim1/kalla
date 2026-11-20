- **The guides ask for one DNS record:** a wildcard record and a wildcard certificate for a sub-domain of the
  installation's own cover every host name (`docs/portainer.md`, *What you need*; `docs/production-setup.md`,
  *Reverse proxy and PKI*). Each service keeps a host name of its own; a test host needs only its own name.
