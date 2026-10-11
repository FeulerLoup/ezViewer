# 发布便携单文件版到 dist\ezViewer.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Test             # 同时运行自测
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.2.0    # 指定版本号（默认取 ezViewer.csproj 中的 Version）
param([switch]$Test, [string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# 依次尝试：用户目录安装的 SDK、系统安装的 SDK、PATH 中的 dotnet
$dotnet = $null
foreach ($candidate in @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:USERPROFILE\.dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe", 'dotnet')) {
    try {
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match '^(8|9|1\d)\.' })) { $dotnet = $candidate; break }
    } catch { }
}
if (-not $dotnet) {
    throw '没有找到 .NET 8 或更高版本的 SDK。可运行：irm https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1; .\dotnet-install.ps1 -Channel 8.0'
}
Write-Host "使用 SDK: $dotnet"

if ($Test) {
    & $dotnet run --project "$root\tests\ezViewer.SelfTest\ezViewer.SelfTest.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw '自测失败' }
}

$publishArgs = @("$root\src\ezViewer\ezViewer.csproj", '-c', 'Release', '-o', "$root\dist", '-nologo')
if ($Version) { $publishArgs += "-p:Version=$Version" }
& $dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw '发布失败' }

$exe = Get-Item "$root\dist\ezViewer.exe"
Write-Host ("完成：{0}  ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB))
