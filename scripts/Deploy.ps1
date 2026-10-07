param([string]$RunDirectory)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding
$projectRoot = Split-Path -Parent $PSScriptRoot
$logRoot = Join-Path $projectRoot 'artifacts\logs'
$timeZone = [TimeZoneInfo]::FindSystemTimeZoneById('SE Asia Standard Time')
$started = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::Now, $timeZone)
[xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if (-not $RunDirectory) { $RunDirectory = Join-Path $logRoot ($started.ToString('yyyyMMdd-HHmmss') + '-' + $version) }
$RunDirectory = [IO.Path]::GetFullPath($RunDirectory)
if (Test-Path -LiteralPath (Join-Path $RunDirectory 'test.log')) { throw 'Thư mục đã có log triển khai. Dùng thư mục mới để giữ nguyên lịch sử.' }
New-Item -ItemType Directory -Path $RunDirectory -Force | Out-Null
$relativeDirectory = 'artifacts/logs/' + (Split-Path -Leaf $RunDirectory)
$journal = Join-Path $projectRoot 'docs\NhatKyTrienKhai.md'
$utf8 = New-Object System.Text.UTF8Encoding($false)
if (-not (Test-Path -LiteralPath $journal)) { [IO.File]::WriteAllText($journal, "# Nhật ký triển khai ShareManHinhDT`r`n", $utf8) }
$results = New-Object System.Collections.Generic.List[object]
$entry = "`r`n## $($started.ToString('yyyy-MM-dd HH:mm:ss zzz')) — bản $version`r`n`r`n"
$entry += "Múi giờ Asia/Saigon. Bắt đầu kiểm thử và tạo gói phát hành; nhật ký thô: ``$relativeDirectory/``.`r`n"
[IO.File]::AppendAllText($journal, $entry, $utf8)

function Invoke-LoggedStep([string]$name, [string]$scriptName) {
    $scriptPath = Join-Path $PSScriptRoot $scriptName
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = (Join-Path $PSHOME 'powershell.exe')
    $startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    $startInfo.WorkingDirectory = $projectRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = 'Hidden'
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    Write-Output "Đang chạy scripts/$scriptName"
    try {
        [void]$process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
        $logFile = Join-Path $RunDirectory ($name + '.log')
        [IO.File]::WriteAllText($logFile, $stdout + "`r`n" + $stderr, $utf8)
        $results.Add([PSCustomObject]@{ Step = $name; Command = "scripts/$scriptName"; ExitCode = $exitCode; Log = "$relativeDirectory/$name.log" })
        Write-Output $stdout
        if ($stderr) { Write-Output $stderr }
        if ($exitCode -ne 0) { throw "Bước $name thất bại, mã thoát $exitCode. Xem $logFile" }
    } finally { $process.Dispose() }
}

$completed = $false
$failureReason = $null
try {
    Invoke-LoggedStep 'test' 'Test.ps1'
    Invoke-LoggedStep 'publish' 'Publish.ps1'
    Invoke-LoggedStep 'verify' 'Verify-Release.ps1'
    $completed = $true
} catch {
    $failureReason = $_.Exception.Message
    throw
} finally {
    $finished = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::Now, $timeZone)
    $status = if ($completed) { 'Hoàn tất kiểm thử tự động, đóng gói và xác minh phát hành.' } else { 'Chưa hoàn tất; có bước thất bại. Sửa lỗi và chạy lại, giữ nguyên nhật ký lần này.' }
    $report = "`r`n$status Kết thúc: $($finished.ToString('yyyy-MM-dd HH:mm:ss zzz')).`r`n`r`n| Lệnh | Mã thoát | Log |`r`n|---|---|---|`r`n"
    foreach ($result in $results) { $report += "| ``$($result.Command)`` | $($result.ExitCode) | [$($result.Step)](../$($result.Log)) |`r`n" }
    if ($failureReason) { $report += "`r`nLỗi triển khai: $failureReason`r`n" }
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDirectory 'steps.json') -Encoding UTF8
    if ($completed) {
        $releaseRoot = Join-Path $projectRoot 'artifacts\release'
        $checksums = Get-Content -LiteralPath (Join-Path $releaseRoot 'checksums.json') -Raw | ConvertFrom-Json
        $report += "`r`n| Gói | Số byte | SHA-256 |`r`n|---|---|---|`r`n"
        foreach ($artifact in $checksums) { $report += "| [$($artifact.File)](../artifacts/release/$($artifact.File)) | $($artifact.Size) | ``$($artifact.SHA256)`` |`r`n" }
        $report += "`r`nAPK được kiểm tra cùng vân tay khóa ký nội bộ hiện có; Windows kèm runtime khởi động thành công. Không phân phối khóa hoặc mật khẩu ký.`r`n"
    }
    $report += "`r`nChưa nghiệm thu điện thoại thật Android 16, truyền hình 60 giây, FPS/độ trễ và PC nhiều màn hình/DPI. Kết quả tự động không thay thế nghiệm thu thiết bị thật.`r`n"
    [IO.File]::AppendAllText($journal, $report, $utf8)
}
