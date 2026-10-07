param([switch]$WindowsOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding
$toolsRoot = Join-Path $projectRoot '.tools'
$dotnetPath = Join-Path $toolsRoot 'dotnet\dotnet.exe'
$releaseRoot = Join-Path $projectRoot 'artifacts\release'
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Chạy scripts/Initialize-Tools.ps1 trước.' }
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Push-Location $projectRoot
try {
    & $dotnetPath publish 'src/ShareManHinhDT.Windows/ShareManHinhDT.Windows.csproj' -c Release -r win-x64 --self-contained true -o (Join-Path $releaseRoot 'windows') -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Đóng gói Windows thất bại.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $releaseRoot 'windows\THIRD-PARTY-NOTICES.md') -Force
    $licenseRoot = Join-Path $releaseRoot 'windows\licenses'
    New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\ZXing.Net-LICENSE.txt') -Destination $licenseRoot -Force
    Compress-Archive -LiteralPath (Join-Path $releaseRoot 'windows') -DestinationPath (Join-Path $releaseRoot 'ShareManHinhDT-Windows-x64.zip') -Force
    if (-not $WindowsOnly) {
        $signingRoot = Join-Path $toolsRoot 'signing'
        New-Item -ItemType Directory -Path $signingRoot -Force | Out-Null
        $keyStore = Join-Path $signingRoot 'internal-release.keystore'
        $passwordFile = Join-Path $signingRoot 'password.txt'
        $keyPasswordFile = Join-Path $signingRoot 'key-password.txt'
        if (-not (Test-Path -LiteralPath $keyStore)) {
            $randomBytes = New-Object byte[] 32
            $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
            try { $rng.GetBytes($randomBytes) } finally { $rng.Dispose() }
            [System.IO.File]::WriteAllText($passwordFile, [Convert]::ToBase64String($randomBytes))
            & (Join-Path $toolsRoot 'jdk\bin\keytool.exe') -genkeypair -keystore $keyStore -alias sharemanhinhdt -keyalg RSA -keysize 3072 -validity 3650 -storetype PKCS12 -storepass:file $passwordFile -keypass:file $passwordFile -dname 'CN=ShareManHinhDT Internal, O=ShareManHinhDT, C=VN'
            if ($LASTEXITCODE -ne 0) { throw 'Không tạo được khóa ký APK.' }
        }
        if (-not (Test-Path -LiteralPath $passwordFile)) { throw 'Thiếu mật khẩu khóa ký. Khôi phục bản sao lưu, không tạo khóa mới để cập nhật APK cũ.' }
        if (-not (Test-Path -LiteralPath $keyPasswordFile)) { Copy-Item -LiteralPath $passwordFile -Destination $keyPasswordFile }
        & $dotnetPath publish 'src/ShareManHinhDT.Android/ShareManHinhDT.Android.csproj' -c Release -p:UseDefaultPublishRuntimeIdentifier=false "-p:AndroidSdkDirectory=$(Join-Path $toolsRoot 'android-sdk')" "-p:JavaSdkDirectory=$(Join-Path $toolsRoot 'jdk')" -p:AndroidKeyStore=true "-p:AndroidSigningKeyStore=$keyStore" -p:AndroidSigningKeyAlias=sharemanhinhdt "-p:AndroidSigningStorePass=file:$passwordFile" "-p:AndroidSigningKeyPass=file:$keyPasswordFile"
        if ($LASTEXITCODE -ne 0) { throw 'Đóng gói Android thất bại.' }
        $signedApk = Join-Path $projectRoot 'src\ShareManHinhDT.Android\bin\Release\net10.0-android36.0\publish\vn.sharemanhinhdt.app-Signed.apk'
        if (-not (Test-Path -LiteralPath $signedApk)) { throw 'Không tìm thấy APK đã ký.' }
        Copy-Item -LiteralPath $signedApk -Destination (Join-Path $releaseRoot 'ShareManHinhDT-Android.apk') -Force
    }
    $manifest = Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object { $_.Extension -in '.apk', '.zip' } | ForEach-Object {
        [PSCustomObject]@{ File = $_.Name; Size = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseRoot 'checksums.json') -Encoding UTF8
} finally { Pop-Location }
