# Build Happ Accessible and create Inno Setup installer.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root "HappAccessible\HappAccessible.csproj"))) {
    $root = $PSScriptRoot
    if (-not (Test-Path (Join-Path $root "HappAccessible\HappAccessible.csproj"))) {
        $root = Split-Path -Parent $PSScriptRoot
    }
}

$proj = Join-Path $root "HappAccessible\HappAccessible.csproj"
$publishDir = Join-Path $root "publish\app"
$iss = Join-Path $root "installer\HappAccessible.iss"
$dist = Join-Path $root "dist"

Write-Host "==> Publishing self-contained win-x64…"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup)."
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Write-Host "==> Compiling installer with $iscc …"
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem $dist -Filter "HappAccessible-Setup-*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 3 |
    ForEach-Object { Write-Host ("Built: " + $_.FullName + " (" + [math]::Round($_.Length/1MB, 1) + " MB)") }
