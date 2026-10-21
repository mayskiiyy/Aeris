param([string]$IsccPath)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0' }
if (-not $IsccPath) {
    $candidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}
if (-not $IsccPath) { throw 'Install Inno Setup 6 (winget install JRSoftware.InnoSetup) or pass -IsccPath.' }

$project = 'src/Aeris/Aeris.csproj'
$cli = 'src/Aeris.Cli/Aeris.Cli.csproj'
$version = ([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version in Directory.Build.props: $version" }

$artifacts = Join-Path $PSScriptRoot 'artifacts'
$portable = Join-Path $artifacts 'Aeris-win-x64'
if (Test-Path -LiteralPath $artifacts) { Remove-Item -LiteralPath $artifacts -Recurse -Force }
New-Item -ItemType Directory -Path $portable | Out-Null

& dotnet publish $project -c Release -r win-x64 --self-contained true -o $portable --nologo --source https://api.nuget.org/v3/index.json `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

& dotnet publish $cli -c Release -r win-x64 --self-contained true -o $portable --nologo --source https://api.nuget.org/v3/index.json `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true `
    -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish of aeris-cli failed.' }
& (Join-Path $portable 'aeris-cli.exe') categories --json | Out-Null
if ($LASTEXITCODE -gt 1) { throw "aeris-cli smoke test failed with exit code $LASTEXITCODE." }

$assets = Get-Content -LiteralPath 'src/Aeris/obj/project.assets.json' -Raw | ConvertFrom-Json
$packages = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$licenses = New-Item -ItemType Directory -Path (Join-Path $portable 'licenses')
$runtimes = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
    Where-Object { $_.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$' }
foreach ($runtime in $runtimes) {
    $runtimeVersion = $runtime.version.Trim('[', ']').Split(',')[0].Trim()
    $folder = Join-Path $packages "$($runtime.name.ToLowerInvariant())\$runtimeVersion"
    $notices = Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$'
    if (-not $notices) { throw "Missing license files for $($runtime.name)" }
    foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses "$($runtime.name)-$($notice.Name)") }
}
Copy-Item -LiteralPath LICENSE -Destination $portable

Compress-Archive -Path "$portable\*" -DestinationPath (Join-Path $artifacts "Aeris-$version-win-x64.zip")
Copy-Item -LiteralPath (Join-Path $portable 'aeris-cli.exe') -Destination (Join-Path $artifacts "aeris-cli-$version-win-x64.exe")
& $IsccPath /Q "/DAppVersion=$version" packaging/Aeris.iss
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

Get-ChildItem -LiteralPath $artifacts -File |
    Get-FileHash -Algorithm SHA256 |
    ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) } |
    Set-Content -Encoding ascii (Join-Path $artifacts 'SHA256SUMS.txt')

Write-Host "Aeris $version is ready in $artifacts"
