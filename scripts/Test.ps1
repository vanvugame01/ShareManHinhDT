$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Chạy scripts/Initialize-Tools.ps1 trước.' }
Push-Location $projectRoot
try {
    & $dotnetPath run --project 'tests/ShareManHinhDT.Tests/ShareManHinhDT.Tests.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Kiểm thử giao thức thất bại.' }
    & $dotnetPath run --project 'tests/ShareManHinhDT.Windows.Tests/ShareManHinhDT.Windows.Tests.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Kiểm thử tích hợp Windows thất bại.' }
} finally { Pop-Location }
