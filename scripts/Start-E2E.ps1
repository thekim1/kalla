<#
.SYNOPSIS
  Starts the local Aspire stack on a separate, disposable test database for the browser tests (tests/e2e).
.DESCRIPTION
  Uses its own Docker volume (ume-rag-e2e-postgres), so your development database is never touched.
  Every start deletes that test volume first and seeds the demo data again, so the tests always begin
  from the same state. The FakeGateway is always on, whatever the user secrets say, and the fixture website of tests/e2e runs on port 7416. The stack uses the same ports as the dev stack: stop the dev stack first.
  Waits for the admin UI, or for the admin API while src/admin-ui does not exist yet.
  -Stop stops the stack and removes both test volumes.
  -Keep starts without resetting the test volume (the demo data is only seeded into an empty database).
  -Setup starts the setup wizard's stack instead (tests/e2e, --project=setup): Setup:Try on a volume of its own
  (ume-rag-e2e-setup-postgres), always reset, so rag-admin starts without a sign-in and writes a setup code to its output.
  It runs no parser, renderer, ClamAV or Open WebUI, which the wizard does not use.
#>
[CmdletBinding()]
param([switch]$Stop, [switch]$Keep, [switch]$Setup)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apphost = [IO.Path]::Combine($root, 'src', 'Ume.Rag.AppHost', 'Ume.Rag.AppHost.csproj')
# The AppHost registers admin-ui only when src/admin-ui/package.json exists (P0 checklist #11).
$last = if (Test-Path ([IO.Path]::Combine($root, 'src', 'admin-ui', 'package.json'))) { 'admin-ui' } else { 'adminapi' }
$normalVolume = 'ume-rag-e2e-postgres'
$setupVolume = 'ume-rag-e2e-setup-postgres'
$volume = if ($Setup) { $setupVolume } else { $normalVolume }

function Remove-TestVolume([string]$name) {
    # The database container can take a few seconds to go away after "aspire stop".
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        if (-not (docker volume ls --quiet --filter "name=^$name`$")) { return }
        $ErrorActionPreference = 'Continue'
        docker volume rm $name 2>&1 | Out-Null
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 2
    }
    throw "Could not remove the test volume $name. Is a stack still using it?"
}

$ErrorActionPreference = 'Continue'
aspire stop --apphost $apphost --non-interactive 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'
if ($Stop) {
    Remove-TestVolume $normalVolume
    Remove-TestVolume $setupVolume
    Write-Output 'Test stack stopped and test databases removed.'
    exit 0
}

# A finished setup closes for good on its database, so the setup stack always starts on a fresh one.
if ($Setup -or -not $Keep) { Remove-TestVolume $volume }
$env:DevelopmentVolumes__Postgres = $volume
$env:Seed__Enabled = 'true'
$env:FakeGateway__Enabled = 'true'   # the tests send real gateway requests; environment variables override user secrets
if (-not $Setup) {
    $env:E2E__Enabled = 'true'          # the fixture website (port 7416); user secrets for real systems are ignored
    Write-Output "Starting the test stack with demo data on volume $volume."
    aspire start --apphost $apphost --non-interactive
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    aspire wait $last --apphost $apphost
    exit $LASTEXITCODE
}

# Set for this start only: the AppHost reads them when it starts, and a later start from the same session must not inherit them.
$setupEnvironment = @{ Setup__Try = 'true'; Parser__Mode = 'off'; Render__Mode = 'off'; ClamAv__Mode = 'off'; OpenWebUi__Enabled = 'false' }
$previous = @{}
foreach ($name in $setupEnvironment.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $setupEnvironment[$name])
}
Write-Output "Starting the setup wizard's test stack on volume $volume."
try {
    aspire start --apphost $apphost --non-interactive
    $started = $LASTEXITCODE
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
}
if ($started -ne 0) { exit $started }
aspire wait $last --apphost $apphost
exit $LASTEXITCODE
