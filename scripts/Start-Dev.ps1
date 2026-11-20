<#
.SYNOPSIS
  Starts the local Aspire stack with or without demo data.
.DESCRIPTION
  (default)  seeds the demo data: the BGE-M3 embedding profile, demo departments, the public example collection with
             the smoke documents, Demo-assistent and the Open WebUI (dev) client with its dev key.
  -Empty     starts without demo data. Only the Keycloak test users exist, so you create collections, sources,
             assistants and clients yourself.
  Seeding only runs against an EMPTY database. To switch an existing environment between demo and
  empty, stop the stack and delete the AppHost's Postgres Docker volume first (see README).
  Waits for the admin UI, or for the admin API while src/admin-ui does not exist yet.
#>
[CmdletBinding()]
param([switch]$Empty)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apphost = [IO.Path]::Combine($root, 'src', 'Kalla.AppHost', 'Kalla.AppHost.csproj')
# The AppHost registers admin-ui only when src/admin-ui/package.json exists (P0 checklist #11).
$last = if (Test-Path ([IO.Path]::Combine($root, 'src', 'admin-ui', 'package.json'))) { 'admin-ui' } else { 'adminapi' }

$env:Seed__Enabled = if ($Empty) { 'false' } else { 'true' }
Write-Output ('Starting with {0}.' -f $(if ($Empty) { 'an empty database (no demo data)' } else { 'demo data' }))
aspire start --apphost $apphost --non-interactive
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
aspire wait $last --apphost $apphost
