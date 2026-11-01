# Builds the signed Android APK (src/MinterWallet.Android -> artifacts/wallet-android/MinterWallet.apk).
# Uses the user-local .NET SDK root with the Android workload when installed, Android Studio's JBR as JDK, and the
# user-local Android SDK. Pass -Install to install onto a connected Android phone or emulator via adb.
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$signingEnvironment = @{}
foreach ($name in 'MINTERWALLET_ANDROID_KEYSTORE','MINTERWALLET_ANDROID_KEY_ALIAS','MINTERWALLET_ANDROID_KEY_PASSWORD','MINTERWALLET_ANDROID_STORE_PASSWORD','DOTNET_ROOT') {
    $signingEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
Push-Location $root
try {
    $dotnet = 'dotnet'
    $localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $localDotnet) {
        $dotnet = $localDotnet
        $env:DOTNET_ROOT = Split-Path $localDotnet -Parent
    }

    $androidSdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
    if (-not (Test-Path $androidSdk)) { throw "Android SDK not found at $androidSdk" }

    $jdk = if ($env:JAVA_HOME -and (Test-Path "$env:JAVA_HOME\bin\javac.exe")) { $env:JAVA_HOME }
           elseif (Test-Path 'C:\Program Files\Android\Android Studio\jbr\bin\javac.exe') { 'C:\Program Files\Android\Android Studio\jbr' }
           else { throw 'JDK with javac.exe not found (install Android Studio or set JAVA_HOME)' }

    if ($Configuration -eq 'Release') { . "$PSScriptRoot\android-signing.ps1" -Jdk $jdk }

    & $dotnet build src/MinterWallet.Android/MinterWallet.Android.csproj `
        -c $Configuration `
        "-p:AndroidSdkDirectory=$androidSdk" `
        "-p:JavaSdkDirectory=$jdk" `
        -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Android APK build failed' }

    $apk = Get-ChildItem "src/MinterWallet.Android/bin/$Configuration" -Recurse -Filter '*-Signed.apk' |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $apk) { throw 'Signed APK not found in build output' }

    $outDir = Join-Path $root 'artifacts\wallet-android'
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $dest = Join-Path $outDir 'MinterWallet.apk'
    Copy-Item $apk.FullName $dest -Force
    Write-Host ('{0,-40} {1,6:N1} MB' -f 'artifacts\wallet-android\MinterWallet.apk', ((Get-Item $dest).Length / 1MB))

    # One Android 9+ APK supports fresh installs and updates, including the former signing certificate.
    $lineage = $env:MINTERWALLET_ANDROID_SIGNING_LINEAGE
    if (-not $lineage -and $env:MINTERWALLET_ANDROID_KEYSTORE) { $lineage = Join-Path (Split-Path $env:MINTERWALLET_ANDROID_KEYSTORE -Parent) 'release.lineage' }
    if ($Configuration -eq 'Release' -and $lineage -and (Test-Path $lineage)) {
        $signer = Get-ChildItem (Join-Path $androidSdk 'build-tools') -Directory | Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'lib\apksigner.jar' } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $signer) { throw 'apksigner.jar not found.' }
        $rotated = Join-Path $outDir 'MinterWallet-rotated.tmp.apk'
        & "$jdk\bin\java.exe" --enable-native-access=ALL-UNNAMED -jar $signer sign `
            --ks $env:MINTERWALLET_ANDROID_KEYSTORE --ks-key-alias $env:MINTERWALLET_ANDROID_KEY_ALIAS `
            --ks-pass env:MINTERWALLET_ANDROID_STORE_PASSWORD --key-pass env:MINTERWALLET_ANDROID_KEY_PASSWORD `
            --lineage $lineage --rotation-min-sdk-version 28 --min-sdk-version 28 `
            --v1-signing-enabled false --v2-signing-enabled false --v3-signing-enabled true --v4-signing-enabled false --out $rotated $dest
        if ($LASTEXITCODE -ne 0) { throw 'Signing certificate rotation failed.' }
        & "$jdk\bin\java.exe" --enable-native-access=ALL-UNNAMED -jar $signer verify --min-sdk-version 28 $rotated
        if ($LASTEXITCODE -ne 0) { throw 'Rotated APK verification failed.' }
        Move-Item -LiteralPath $rotated -Destination $dest -Force
        Write-Host 'One Android 9+ APK is ready for installation and updates.'
    }
    foreach ($oldName in 'MinterWallet-upgrade-android9plus.apk', 'MinterWallet-upgrade-android9plus.apk.idsig') {
        $oldFile = Join-Path $outDir $oldName
        if (Test-Path -LiteralPath $oldFile) { Remove-Item -LiteralPath $oldFile }
    }
    $checksum = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $outDir 'SHA256SUMS.txt'), "$checksum  MinterWallet.apk`n", [Text.UTF8Encoding]::new($false))

    if ($Install) {
        $adb = Join-Path $androidSdk 'platform-tools\adb.exe'
        if (-not (Test-Path $adb)) { $adb = 'adb' }
        $deviceApi = & $adb shell getprop ro.build.version.sdk
        if ($LASTEXITCODE -ne 0 -or $deviceApi -notmatch '^\d+$') { throw 'Could not identify one connected Android device.' }
        if ([int]$deviceApi -lt 28) { throw 'Android 9 or newer is required.' }
        & $adb install -r $dest
        if ($LASTEXITCODE -ne 0) { throw 'adb install failed' }
    }
}
finally {
    foreach ($name in $signingEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $signingEnvironment[$name]) }
    Pop-Location
}
