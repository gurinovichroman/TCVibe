<#
.SYNOPSIS
    Build and package TCVibe as a self-updating portable ZIP (Velopack, ADR-0031).

.DESCRIPTION
    1. Publishes TCV.App self-contained for the runtime.
    2. Packs it with vpk (the version pinned in dotnet-tools.json) into artifacts\releases\<runtime>:
       the full package, a delta against the previous full package found there, the feed
       (releases.<channel>.json) and the portable ZIP.
    3. Copies the portable ZIP to artifacts\TCVibe-v<version>-<runtime>-portable.zip.

    The ZIP unpacks to TCVibe.exe (start this), Update.exe and current\ (the binaries, replaced by
    every update). The state goes to data\ beside them on the first start. current\ also carries the
    licence, the third-party notices and libgit2's own licence.

.PARAMETER Version
    Version to stamp (default: <Version> from Directory.Build.props).

.PARAMETER Runtime
    win-x64 (default) or win-x86. It is also the Velopack channel: each architecture updates from
    its own feed.

.PARAMETER ReleaseNotes
    Optional markdown file shown by the update prompt.

.EXAMPLE
    .\publish-portable.ps1
    .\publish-portable.ps1 -Version "0.2.0" -Runtime "win-x86"
#>
param(
    [string]$Version = "",
    [string]$Runtime = "win-x64",
    [string]$ReleaseNotes = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Resolve paths ────────────────────────────────────────────────────────────
$Root       = Split-Path $PSScriptRoot -Parent
$AppProject = Join-Path $Root "src\TCV.App\TCV.App.csproj"
$Artifacts  = Join-Path $Root "artifacts"
$PublishDir = Join-Path $Artifacts "staging\$Runtime"
$Releases   = Join-Path $Artifacts "releases\$Runtime"
$Channel    = $Runtime

# ── Read version from Directory.Build.props if not supplied ─────────────────
if (-not $Version) {
    [xml]$xml = Get-Content (Join-Path $Root "Directory.Build.props")
    $versionNode = $xml.SelectSingleNode("//Version")
    if ($versionNode) { $Version = $versionNode.InnerText }
    if (-not $Version) { throw "No <Version> in Directory.Build.props" }
}

Write-Host "Building TCVibe v$Version ($Runtime) ..." -ForegroundColor Cyan

# ── dotnet publish ───────────────────────────────────────────────────────────
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
dotnet publish $AppProject `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $PublishDir `
    -p:Version=$Version `
    -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$ReadmeSrc = Join-Path $Root "README.md"
if (Test-Path $ReadmeSrc) { Copy-Item $ReadmeSrc (Join-Path $PublishDir "README.md") }

# ── Licences that travel with the binaries ───────────────────────────────────
# MIT and Apache-2.0 require their notices next to the binaries, not only in the repository.
Copy-Item (Join-Path $Root "LICENSE") (Join-Path $PublishDir "LICENSE.txt")
Copy-Item (Join-Path $Root "THIRD-PARTY-NOTICES.txt") $PublishDir
# libgit2's licence (GPL-2.0 with the linking exception, plus what it bundles) is taken from the
# package this build resolved, so it always matches the shipped git2-*.dll.
$Assets = Get-Content (Join-Path $Root "src\TCV.App\obj\project.assets.json") -Raw | ConvertFrom-Json
$NativeGit = $Assets.libraries.PSObject.Properties | Where-Object { $_.Name -like 'LibGit2Sharp.NativeBinaries/*' } | Select-Object -First 1
if (-not $NativeGit) { throw "LibGit2Sharp.NativeBinaries is not in project.assets.json" }
$PackageRoot = $Assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$Licenses = Join-Path $PublishDir "licenses"
New-Item -ItemType Directory -Force $Licenses | Out-Null
Copy-Item (Join-Path $PackageRoot "$($NativeGit.Value.path)\libgit2\libgit2.license.txt") (Join-Path $Licenses "libgit2.txt")

# ── vpk pack ─────────────────────────────────────────────────────────────────
Push-Location $Root
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

    # vpk refuses a version its feed folder already holds; a rebuild of the same version replaces it
    # (vpk rebuilds releases.<channel>.json from the packages it finds).
    if (Test-Path $Releases) {
        Get-ChildItem $Releases -Filter "TCVibe-$Version-$Channel-*.nupkg" | Remove-Item -Force
    }

    $pack = @(
        'vpk', 'pack',
        # The id is how an installed copy finds its updates: changing it strands every copy out there.
        '--packId', 'TCVibe',
        '--packVersion', $Version,
        '--packDir', $PublishDir,
        '--mainExe', 'TCV.App.exe',
        '--packTitle', 'TCVibe',
        '--packAuthors', 'TCVibe',
        # Update.exe's icon; the stub TCVibe.exe takes the main exe's (ApplicationIcon).
        '--icon', (Join-Path $Root "src\TCV.App\Assets\Logo\tcv.ico"),
        # Without it vpk assumes x86: the x64 package would get a 32-bit Update.exe and stub.
        '--runtime', $Runtime,
        '--channel', $Channel,
        '--noInst',
        '--outputDir', $Releases
    )
    if ($ReleaseNotes) { $pack += @('--releaseNotes', $ReleaseNotes) }
    dotnet @pack
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

# ── The portable ZIP under its usual name ────────────────────────────────────
$Portable = Join-Path $Releases "TCVibe-$Channel-Portable.zip"
$ZipPath  = Join-Path $Artifacts "TCVibe-v$Version-$Runtime-portable.zip"
Copy-Item $Portable $ZipPath -Force

$SizeMB = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "  Published: $ZipPath ($SizeMB MB)" -ForegroundColor Green
Write-Host "  Feed:      $Releases" -ForegroundColor Green
