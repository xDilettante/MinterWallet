# Installs a separate QA app using the production biometric service and a public test string.
# Does not read, unlock, reset or uninstall the wallet. Fingerprint confirmation is performed by the user.
param([switch]$ReadResults)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $env:LOCALAPPDATA 'Android/Sdk'
$adb = Join-Path $sdk 'platform-tools/adb.exe'
$package = 'network.minter.wallet.biometriccheck'
if ($ReadResults) {
    & $adb shell run-as $package cat files/diagnostics.txt
    if ($LASTEXITCODE -ne 0) { throw 'No native biometric check results yet.' }
    exit
}
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
Push-Location $root
try {
    & $dotnet build tests/MinterWallet.AndroidChecks -c Debug "-p:AndroidSdkDirectory=$sdk" `
        '-p:JavaSdkDirectory=C:/Program Files/Android/Android Studio/jbr' -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native biometric QA build failed.' }
    $apk = Get-ChildItem tests/MinterWallet.AndroidChecks/bin/Debug -Recurse -Filter '*-Signed.apk' |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $apk) { throw 'Native biometric QA APK not found.' }
    & $adb install -r $apk.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Native biometric QA installation failed.' }
    $activity = & $adb shell cmd package resolve-activity --brief $package | Select-Object -Last 1
    if ($activity -notmatch '^network\.minter\.wallet\.biometriccheck/') { throw 'QA activity not found.' }
    & $adb shell am start -n $activity.Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not open native biometric QA.' }
    Write-Host 'On the phone: 1 Enable, 2 Read; confirm each with a fingerprint. Read results with -ReadResults.'
}
finally { Pop-Location }
