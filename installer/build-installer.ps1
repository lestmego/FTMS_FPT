$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\FTMS.Desktop\FTMS.Desktop.csproj'
$publish = Join-Path $PSScriptRoot 'publish'
$dotnetCandidates = @(
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
)
$dotnet = $dotnetCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
$compilerCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $dotnet) {
    throw 'Khong tim thay .NET SDK 8.'
}
if (-not $compiler) {
    throw 'Khong tim thay Inno Setup 6.'
}

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publish

if ($LASTEXITCODE -ne 0) { throw 'Publish ung dung that bai.' }

& $compiler (Join-Path $PSScriptRoot 'FTMS.Companion.iss')
if ($LASTEXITCODE -ne 0) { throw 'Tao bo cai that bai.' }

$output = Get-ChildItem (Join-Path $root 'dist') -Filter 'FTMS-Companion-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Bo cai da tao tai: $($output.FullName)"
