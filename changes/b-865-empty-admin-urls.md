- **rag-admin starts on a Compose install without a domain** (#865): an empty `Admin:GatewayAdminUrl` or
  `Admin:ApiPublicUrl`, as the one service file renders them when no host or domain is set, counts as unset instead of
  failing the URL check at start.
