param($quiet=$true)

$ErrorActionPreference = 'Stop'
$scriptPath =  $MyInvocation.MyCommand.Definition
$classPixelProRoot = "$([System.IO.Path]::GetDirectoryName($scriptPath))\..\..\ClassPixelPro.Desktop" 


function SetEnvironmentVariable {
    param (
        $Name, $Value
    )
    $out = "$Name = $Value"
    Write-Host $out -ForegroundColor DarkGray
    [Environment]::SetEnvironmentVariable($Name, $Value, 1)
}

Set-Location $classPixelProRoot


try {
    $quietparams = ""
    if ($quiet){
        $quietparams = ("-p:WarningLevel=0", "-p:NoWarn=NU1701")
    }
    dotnet --version
    Write-Host "🔧 正在清理…" -ForegroundColor Cyan

    dotnet clean ClassPixelPro.Desktop.csproj
    Write-Host "🔧 正在构建 ClassPixelPro，这可能需要 1-6 分钟。" -ForegroundColor Cyan
    dotnet build ClassPixelPro.Desktop.csproj -c Debug -p:Version=$(git describe --tags --abbrev=0) -p:NuGetVersion=$(git describe --tags --abbrev=0) $quietparams
}
catch {
    Write-Host "🔥 构建失败" -ForegroundColor Red
    return
}


Write-Host "🔧 正在设置开发环境变量…" -ForegroundColor Cyan

[Environment]::SetEnvironmentVariable("ClassPixelPro_DebugBinaryFile", [System.IO.Path]::GetFullPath("${classPixelProRoot}\bin\Debug\net8.0-windows10.0.19041.0\ClassPixelPro.Desktop.exe"), 1)
[Environment]::SetEnvironmentVariable("ClassPixelPro_DebugBinaryDirectory", [System.IO.Path]::GetFullPath("${classPixelProRoot}/bin\Debug\net8.0-windows10.0.19041.0\"), 1)

Write-Host "构建完成" -ForegroundColor Green
