# Dot-source from build-android.ps1. Private signing material stays outside the repository.
# On Windows DPAPI protects the password for the current user; CI supplies environment variables instead.
param([Parameter(Mandatory = $true)][string]$Jdk)
$ErrorActionPreference = 'Stop'
if ($env:MINTERWALLET_ANDROID_KEYSTORE) { return }
$signingDirectory = Join-Path $env:LOCALAPPDATA 'MinterWallet\Signing'
New-Item -ItemType Directory -Force -Path $signingDirectory | Out-Null
$signingUserSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $signingDirectory /inheritance:r /grant:r "*${signingUserSid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict access to the signing directory.' }
$keyFile = Join-Path $signingDirectory 'release.keystore'
$passwordFile = Join-Path $signingDirectory 'release.signing.xml'
if ((Test-Path $keyFile) -xor (Test-Path $passwordFile)) { throw 'Incomplete signing backup. Restore both signing files; no key was replaced.' }
if (-not (Test-Path $keyFile)) {
    $randomPasswordBytes = New-Object byte[] 32
    $signingRandom = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $signingRandom.GetBytes($randomPasswordBytes) } finally { $signingRandom.Dispose() }
    $securePassword = ConvertTo-SecureString ([Convert]::ToBase64String($randomPasswordBytes)) -AsPlainText -Force
    $securePassword | Export-Clixml -LiteralPath $passwordFile
}
else { $securePassword = Import-Clixml -LiteralPath $passwordFile }
$signingPassword = [System.Net.NetworkCredential]::new('', $securePassword).Password
$env:MINTERWALLET_ANDROID_KEYSTORE = $keyFile
$env:MINTERWALLET_ANDROID_KEY_ALIAS = 'minterwallet-release'
$env:MINTERWALLET_ANDROID_STORE_PASSWORD = $signingPassword
$env:MINTERWALLET_ANDROID_KEY_PASSWORD = $signingPassword
if (-not (Test-Path $keyFile)) {
    & "$Jdk\bin\keytool.exe" -genkeypair -keystore $keyFile -alias $env:MINTERWALLET_ANDROID_KEY_ALIAS `
        -storepass:env MINTERWALLET_ANDROID_STORE_PASSWORD -keypass:env MINTERWALLET_ANDROID_KEY_PASSWORD `
        -keyalg RSA -keysize 3072 -sigalg SHA256withRSA -validity 10000 -dname 'CN=Minter Wallet Release' -noprompt
    if ($LASTEXITCODE -ne 0) { throw 'Private signing key generation failed. Inspect the signing directory before retrying.' }
    Write-Host "Created private signing key outside git: $keyFile"
}
$signingPassword = $null
$securePassword = $null
