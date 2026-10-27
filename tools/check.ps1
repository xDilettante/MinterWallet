# Local check of the wallet: build (warnings are errors), protocol checks against the Go and JS fixtures, wallet core
# and API client checks, the single-file wallet for Windows and Linux with its self-test. -Devnet http://WSL-IP:38843
# adds the end-to-end runs against a local devnet node (tools/devnet/start.sh): protocol, wallet core and the headless
# window with screenshots in artifacts/ui-checks. Works in Windows PowerShell 5.1.
param([string]$Devnet = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet build MinterWallet.slnx -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    dotnet run -c Release --no-build --project tests/Minter.Protocol.Checks
    if ($LASTEXITCODE -ne 0) { throw 'protocol checks failed' }

    dotnet run -c Release --no-build --project tests/MinterWallet.Checks
    if ($LASTEXITCODE -ne 0) { throw 'wallet checks failed' }

    foreach ($rid in 'win-x64', 'linux-x64') {
        dotnet publish src/MinterWallet -c Release -r $rid -p:PublishSingleFile=true -o "artifacts/wallet-$rid" -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "wallet publish $rid failed" }
    }
    if ($env:OS -eq 'Windows_NT') {
        $selfTest = & "$root\artifacts\wallet-win-x64\MinterWallet.exe" --self-test | Out-String
        Write-Host $selfTest.Trim()
        if ($LASTEXITCODE -ne 0) { throw 'wallet self-test failed' }
    }
    Get-ChildItem artifacts -Recurse -File | Where-Object { $_.Name -in 'MinterWallet.exe', 'MinterWallet' } |
        ForEach-Object { '{0,-40} {1,6:N1} MB' -f $_.FullName.Substring($root.Length + 1), ($_.Length / 1MB) }

    if ($Devnet -ne '') {
        dotnet run -c Release --no-build --project tests/Minter.Protocol.Checks -- --devnet $Devnet
        if ($LASTEXITCODE -ne 0) { throw 'devnet end-to-end failed' }
        dotnet run -c Release --no-build --project tests/MinterWallet.Checks -- --devnet $Devnet
        if ($LASTEXITCODE -ne 0) { throw 'devnet wallet end-to-end failed' }
        dotnet run -c Release --no-build --project tests/MinterWallet.UiChecks -- --devnet $Devnet
        if ($LASTEXITCODE -ne 0) { throw 'devnet UI checks failed' }
    }
    Write-Host 'All checks passed.'
}
finally { Pop-Location }
