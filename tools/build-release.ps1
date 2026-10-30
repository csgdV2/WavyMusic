param([switch]$BuildInstaller)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdkCandidates = @(
    (Join-Path $env:USERPROFILE 'AppData/Local/WavyBuild/dotnet/dotnet.exe'),
    (Join-Path $env:LOCALAPPDATA 'WavyBuild/dotnet/dotnet.exe'),
    $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }),
    $(if ($env:ProgramFiles) { Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }),
    (Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'),
    (Join-Path $projectRoot '.dotnet/dotnet.exe'),
    $((Get-Command dotnet.exe -All -ErrorAction SilentlyContinue).Source)
)
$dotnet = $null
foreach ($candidate in $sdkCandidates | Where-Object { $_ } | Select-Object -Unique) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    try {
        $installedSdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($installedSdks -match '^8\.0\.\d+\s')) {
            $dotnet = $candidate
            break
        }
    }
    catch { }
}
if (-not $dotnet) { throw 'The .NET 8 SDK was not found. Install the x64 .NET 8 SDK, reopen the terminal, then try again.' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
Write-Output "Using .NET 8 SDK from $dotnet"
$publishFolder = Join-Path $projectRoot 'publish/win-x64'
& $dotnet publish (Join-Path $projectRoot 'src/Wavy/Wavy.csproj') -c Release -p:Platform=x64 --self-contained true -o $publishFolder
if ($LASTEXITCODE -ne 0) { throw 'Wavy publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishFolder 'App.xbf'))) { throw 'Published XAML resources are missing.' }
if ($BuildInstaller) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $compilerCandidates = @(
        "$env:LOCALAPPDATA/Programs/Inno Setup 7/ISCC.exe",
        'C:/Program Files/Inno Setup 7/ISCC.exe',
        $(if ($compiler) { $compiler.Source }),
        "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe",
        "$env:LOCALAPPDATA/WavyBuild/InnoSetup6/ISCC.exe",
        'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
    )
    $compilerPath = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $compilerPath) { throw 'Install Inno Setup from jrsoftware.org, then rerun with -BuildInstaller.' }
    & $compilerPath (Join-Path $projectRoot 'installer/wavy.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
Write-Output "Wavy 2.0 is ready in $publishFolder"
