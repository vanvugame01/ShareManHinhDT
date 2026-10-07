param([switch]$WindowsOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding
$releaseRoot = Join-Path $projectRoot 'artifacts\release'
$toolsRoot = Join-Path $projectRoot '.tools'
$checksums = Get-Content -LiteralPath (Join-Path $releaseRoot 'checksums.json') -Raw | ConvertFrom-Json
foreach ($entry in $checksums) {
    $artifactPath = Join-Path $releaseRoot $entry.File
    if ((Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash -ne $entry.SHA256) { throw "Sai checksum: $($entry.File)" }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $releaseRoot 'windows\ShareManHinhDT.Windows.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtimeConfig.runtimeOptions.includedFrameworks) { throw 'Gói Windows chưa kèm runtime.' }
[xml]$versionProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$expectedVersion = [string]$versionProperties.Project.PropertyGroup.Version
$windowsAssembly = Join-Path $releaseRoot 'windows\ShareManHinhDT.Windows.dll'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($windowsAssembly).ProductVersion -ne $expectedVersion) { throw 'Sai phiên bản Windows đóng gói.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $releaseRoot 'ShareManHinhDT-Windows-x64.zip'))
try {
    $assemblyEntry = $archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq 'windows/ShareManHinhDT.Windows.dll' } | Select-Object -First 1
    if (-not $assemblyEntry) { throw 'ZIP thiếu assembly Windows.' }
    $stream = $assemblyEntry.Open()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $archiveHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '')
        if ($archiveHash -ne (Get-FileHash -LiteralPath $windowsAssembly -Algorithm SHA256).Hash) { throw 'ZIP khác gói Windows vừa biên dịch.' }
    } finally { $stream.Dispose(); $hasher.Dispose() }
} finally { $archive.Dispose() }
$smokeProcess = Start-Process -FilePath (Join-Path $releaseRoot 'windows\ShareManHinhDT.Windows.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
try {
    if (-not $smokeProcess.WaitForExit(15000)) { $smokeProcess.Kill(); throw 'Khởi động Windows quá thời gian.' }
    $smokeProcess.Refresh()
    if ($smokeProcess.ExitCode -ne 0) { throw "Khởi động Windows thất bại: $($smokeProcess.ExitCode)" }
} finally { $smokeProcess.Dispose() }
if (-not $WindowsOnly) {
    $buildTools = Get-ChildItem -LiteralPath (Join-Path $toolsRoot 'android-sdk\build-tools') -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $buildTools) { throw 'Không tìm thấy Android Build Tools.' }
    $javaPath = Join-Path $toolsRoot 'jdk\bin\java.exe'
    $signatureReport = & $javaPath -jar (Join-Path $buildTools.FullName 'lib\apksigner.jar') verify --verbose --print-certs (Join-Path $releaseRoot 'ShareManHinhDT-Android.apk')
    if ($LASTEXITCODE -ne 0) { throw 'Chữ ký APK không hợp lệ.' }
    $signatureReport | Write-Output
    $expectedFingerprint = '77d7ca5f790f85559eed7169e3769f1cfda00885409453db0d8ad499e539da53'
    if ($signatureReport -notcontains "Signer #1 certificate SHA-256 digest: $expectedFingerprint") { throw 'APK không dùng khóa ký nội bộ hiện có.' }
    $badging = & (Join-Path $buildTools.FullName 'aapt2.exe') dump badging (Join-Path $releaseRoot 'ShareManHinhDT-Android.apk')
    if ($LASTEXITCODE -ne 0) { throw 'Không đọc được manifest APK.' }
    $badging | Select-Object -First 10
    [xml]$androidProject = Get-Content -LiteralPath (Join-Path $projectRoot 'src\ShareManHinhDT.Android\ShareManHinhDT.Android.csproj') -Raw
    $expectedCode = [string]$androidProject.Project.PropertyGroup.ApplicationVersion
    if ($badging[0] -notmatch "versionCode='$expectedCode' versionName='$expectedVersion'") { throw 'Sai phiên bản hoặc versionCode APK.' }
}
Write-Output 'Đạt kiểm tra checksum, đóng gói và khởi động Windows; chữ ký/manifest Android được kiểm tra nếu có.'
