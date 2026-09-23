$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\FTMS.Desktop\FTMS.Desktop.csproj'
$publish = Join-Path $PSScriptRoot 'publish'
$compilerCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $compiler) {
    throw 'Không tìm thấy Inno Setup 6. Hãy cài JRSoftware.InnoSetup trước.'
}

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publish

if ($LASTEXITCODE -ne 0) { throw 'Publish ứng dụng thất bại.' }

& $compiler (Join-Path $PSScriptRoot 'FTMS.Companion.iss')
if ($LASTEXITCODE -ne 0) { throw 'Tạo bộ cài thất bại.' }

Write-Host "Bộ cài đã tạo tại: $(Join-Path $root 'dist\FTMS-Companion-Setup-1.0.0.exe')"
