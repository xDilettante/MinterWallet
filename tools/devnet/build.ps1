# Builds the devnet genesis tool and upstream minter-go-node v3.3.0 for Linux (the node does not build for Windows:
# cmd/minter uses syscall.Rlimit). Output: tools/devnet/bin/devnet-linux, tools/devnet/bin/minter-linux.
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $env:CGO_ENABLED = '0'
    $env:GOOS = 'linux'
    go build -o bin/devnet-linux .
    if ($LASTEXITCODE -ne 0) { throw 'devnet tool build failed' }
    go build -o bin/minter-linux github.com/MinterTeam/minter-go-node/cmd/minter
    if ($LASTEXITCODE -ne 0) { throw 'node build failed' }
    Get-ChildItem bin | Select-Object Name, Length
}
finally {
    Remove-Item Env:GOOS -ErrorAction SilentlyContinue
    Pop-Location
}
