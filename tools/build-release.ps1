param([switch]$BuildInstaller)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
if (-not (& $dotnet --list-sdks)) {
    $localSdk = Join-Path $env:LOCALAPPDATA 'WavyBuild/dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $localSdk)) { throw 'Install the .NET 8 SDK before building Wavy.' }
    $dotnet = $localSdk
}
$publishFolder = Join-Path $projectRoot 'publish/win-x64'
& $dotnet publish (Join-Path $projectRoot 'src/Wavy/Wavy.csproj') -c Release -p:Platform=x64 --self-contained true -o $publishFolder
if ($LASTEXITCODE -ne 0) { throw 'Wavy publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishFolder 'App.xbf'))) { throw 'Published XAML resources are missing.' }
if ($BuildInstaller) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $compilerCandidates = @(
        $(if ($compiler) { $compiler.Source }),
        "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe",
        "$env:LOCALAPPDATA/Programs/Inno Setup 7/ISCC.exe",
        "$env:LOCALAPPDATA/WavyBuild/InnoSetup6/ISCC.exe",
        'C:/Program Files (x86)/Inno Setup 6/ISCC.exe',
        'C:/Program Files/Inno Setup 7/ISCC.exe'
    )
    $compilerPath = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $compilerPath) { throw 'Install Inno Setup from jrsoftware.org, then rerun with -BuildInstaller.' }
    & $compilerPath (Join-Path $projectRoot 'installer/wavy.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
Write-Output "Wavy 1.8 is ready in $publishFolder"
