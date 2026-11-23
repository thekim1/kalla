- **The gateway's organisation logo shows in the admin UI**: the menu, the phone header, the sign-in page and the setup
  wizard showed the organisation name from the gateway's context but always the built-in mark, because the gateway
  exposed its admin console's logo only to its own console. With a gateway that offers it (feature
  `organisation-logo`, thekim1/fors#126), kalla-admin reads the logo from the gateway's Integration API with
  its credential key (no scope needed), holds it for 5 minutes and serves it from its own origin
  (`/bff/branding/logo`, named as `logoUrl` in `/bff/user`), so the Content-Security-Policy is unchanged. Without the
  feature, the Integration API address or a logo, the built-in mark stays, and a failed read never stops the page
  loading. A wide logo keeps its shape. No configuration change.
- **The admin UI's favicon is a "K"** on the same green, in place of the "U" mark the gateway's console has too, so the
  two tabs can be told apart.
