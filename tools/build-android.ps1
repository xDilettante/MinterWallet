# Builds the signed Android APK (src/MinterWallet.Android -> artifacts/wallet-android/MinterWallet.apk).
# Uses the user-local .NET SDK root with the Android workload when installed, Android Studio's JBR as JDK, and the
# user-local Android SDK. Pass -Install to install onto a connected Android phone or emulator via adb.
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
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

    if ($Install) {
        $adb = Join-Path $androidSdk 'platform-tools\adb.exe'
        if (-not (Test-Path $adb)) { $adb = 'adb' }
        & $adb install -r $dest
        if ($LASTEXITCODE -ne 0) { throw 'adb install failed' }
    }
}
finally {
    Pop-Location
}
