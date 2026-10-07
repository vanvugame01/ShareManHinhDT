param([switch]$WindowsOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $projectRoot '.tools'
$dotnetPath = Join-Path $toolsRoot 'dotnet\dotnet.exe'
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $dotnetPath)) {
    $installerPath = Join-Path $toolsRoot 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath
    $sdkVersion = (Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    & $installerPath -Version $sdkVersion -InstallDir (Join-Path $toolsRoot 'dotnet') -NoPath
    if ($LASTEXITCODE -ne 0) { throw 'Không cài được .NET SDK.' }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $projectRoot
try {
    if (-not $WindowsOnly) {
        & $dotnetPath workload install android --skip-manifest-update
        if ($LASTEXITCODE -ne 0) { throw 'Không cài được workload Android.' }
        & $dotnetPath build 'src/ShareManHinhDT.Android/ShareManHinhDT.Android.csproj' -t:InstallAndroidDependencies "-p:AndroidSdkDirectory=$(Join-Path $toolsRoot 'android-sdk')" "-p:JavaSdkDirectory=$(Join-Path $toolsRoot 'jdk')" -p:AcceptAndroidSdkLicenses=True
        if ($LASTEXITCODE -ne 0) { throw 'Không chuẩn bị được Android SDK/JDK.' }
    }
} finally { Pop-Location }
