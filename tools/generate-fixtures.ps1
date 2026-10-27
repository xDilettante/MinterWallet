# Regenerates tests/Minter.Protocol.Checks/Fixtures: cases.json (tools/oracle/cases.mjs), go-expected.json (upstream
# minter-go-node v3.3.0 through tools/oracle) and js-expected.json (official Minter JS libraries, tools/js-oracle).
# Needs Go and Node.js; downloads Go modules and npm packages on the first run.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixtures = Join-Path $root 'tests\Minter.Protocol.Checks\Fixtures'

Push-Location (Join-Path $PSScriptRoot 'oracle')
try {
    $env:CGO_ENABLED = '0'
    go build -o oracle.exe .
    if ($LASTEXITCODE -ne 0) { throw 'oracle build failed' }
}
finally { Pop-Location }

node (Join-Path $PSScriptRoot 'oracle\cases.mjs')
if ($LASTEXITCODE -ne 0) { throw 'cases.mjs failed' }

# cmd /c keeps the bytes of the JSON intact (PowerShell 5.1 pipes would re-encode them).
cmd /c "`"$PSScriptRoot\oracle\oracle.exe`" < `"$fixtures\cases.json`" > `"$fixtures\go-expected.json`""
if ($LASTEXITCODE -ne 0) { throw 'oracle failed' }

$js = Join-Path $PSScriptRoot 'js-oracle'
if (-not (Test-Path (Join-Path $js 'node_modules'))) {
    Push-Location $js
    try { npm install --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm install failed' } }
    finally { Pop-Location }
}
node (Join-Path $js 'vectors.mjs')
if ($LASTEXITCODE -ne 0) { throw 'js oracle failed' }
Write-Host "Fixtures written to $fixtures"
