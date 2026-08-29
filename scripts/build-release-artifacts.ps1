# Build installer + portable zip and copy both to the user's Downloads folder.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\build-release-artifacts.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root "HappAccessible\HappAccessible.csproj"))) {
    $root = Split-Path -Parent $PSScriptRoot
}

$proj = Join-Path $root "HappAccessible\HappAccessible.csproj"
$publishDir = Join-Path $root "publish\app"
$iss = Join-Path $root "installer\HappAccessible.iss"
$dist = Join-Path $root "dist"
$downloads = Join-Path $env:USERPROFILE "Downloads"

# Read the canonical version from the project and synchronize Inno Setup.
$csprojText = Get-Content $proj -Raw
if ($csprojText -notmatch '<Version>([^<]+)</Version>') {
    throw "Could not parse Version from project file."
}
$version = $Matches[1].Trim()
$issText = Get-Content $iss -Raw
$issText = [regex]::Replace(
    $issText,
    '#define MyAppVersion "[^"]+"',
    "#define MyAppVersion `"$version`"")
Set-Content -Path $iss -Value $issText -Encoding UTF8

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

$setupName = "HappAccessible-Setup-$version.exe"
$setupPath = Join-Path $dist $setupName
if (-not (Test-Path $setupPath)) {
    throw "Installer not found: $setupPath"
}

$portableName = "HappAccessible-Portable-$version.zip"
$portablePath = Join-Path $dist $portableName
Write-Host "==> Creating portable zip…"
if (Test-Path $portablePath) { Remove-Item $portablePath -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $portablePath -CompressionLevel Optimal

New-Item -ItemType Directory -Force -Path $downloads | Out-Null
Copy-Item -Force $setupPath (Join-Path $downloads $setupName)
Copy-Item -Force $portablePath (Join-Path $downloads $portableName)

Write-Host "Installer: $setupPath ($([math]::Round((Get-Item $setupPath).Length/1MB, 1)) MB)"
Write-Host "Portable:  $portablePath ($([math]::Round((Get-Item $portablePath).Length/1MB, 1)) MB)"
Write-Host "Copied to: $downloads"
