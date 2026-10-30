<#
.SYNOPSIS
  Starts the local Aspire stack on a separate, disposable test database for the browser tests (tests/e2e).
.DESCRIPTION
  Uses its own Docker volume (ume-rag-e2e-postgres), so your development database is never touched.
  Every start deletes that test volume first and seeds the demo data again, so the tests always begin
  from the same state. The FakeGateway is always on, whatever the user secrets say. The stack uses the same ports as the dev stack: stop the dev stack first.
  Waits for the admin UI, or for the admin API while src/admin-ui does not exist yet.
  -Stop stops the stack and removes the test volume.
  -Keep starts without resetting the test volume (the demo data is only seeded into an empty database).
#>
[CmdletBinding()]
param([switch]$Stop, [switch]$Keep)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apphost = [IO.Path]::Combine($root, 'src', 'Ume.Rag.AppHost', 'Ume.Rag.AppHost.csproj')
# The AppHost registers admin-ui only when src/admin-ui/package.json exists (P0 checklist #11).
$last = if (Test-Path ([IO.Path]::Combine($root, 'src', 'admin-ui', 'package.json'))) { 'admin-ui' } else { 'adminapi' }
$volume = 'ume-rag-e2e-postgres'

function Remove-TestVolume {
    # The database container can take a few seconds to go away after "aspire stop".
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        if (-not (docker volume ls --quiet --filter "name=^$volume`$")) { return }
        $ErrorActionPreference = 'Continue'
        docker volume rm $volume 2>&1 | Out-Null
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 2
    }
    throw "Could not remove the test volume $volume. Is a stack still using it?"
}

$ErrorActionPreference = 'Continue'
aspire stop --apphost $apphost --non-interactive 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'
if ($Stop) { Remove-TestVolume; Write-Output 'Test stack stopped and test database removed.'; exit 0 }

if (-not $Keep) { Remove-TestVolume }
$env:DevelopmentVolumes__Postgres = $volume
$env:Seed__Enabled = 'true'
$env:FakeGateway__Enabled = 'true'   # the tests send real gateway requests; environment variables override user secrets
Write-Output "Starting the test stack with demo data on volume $volume."
aspire start --apphost $apphost --non-interactive
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
aspire wait $last --apphost $apphost
