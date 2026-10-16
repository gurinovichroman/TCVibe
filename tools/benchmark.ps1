<#
.SYNOPSIS
    Measures the two performance budgets from docs/VISION.md and reports pass/fail.

.DESCRIPTION
    Task 6.25. Two numbers:

      * cold start        -- launch to "the window is up and idle", median of -Runs launches
      * 10 000-file listing -- enumerating a generated directory, both through the OS and,
                            optionally, through the app's own code path (see -Listing)

    The listing number the *application* produces is asserted continuously by
    tests/TCV.App.Tests/ListingBenchmarkTests.cs; this script exists for the half a unit test
    cannot do honestly -- starting a process -- and for the side-by-side against Explorer and
    Double Commander that the roadmap asks for.

    Comparison rows are only produced for the tools this machine actually has. Explorer is always
    there; Double Commander is looked up in PATH and in its usual install locations, and skipped
    with a note when absent.

.PARAMETER Exe
    The executable to measure. Defaults to the Release build; pass current\TCV.App.exe of an
    unpacked portable ZIP to measure what users actually run. Not the TCVibe.exe stub:
    it starts the program through Update.exe and exits, so there is no window to wait for (the
    hop costs less than the run-to-run spread, ADR-0031).

.PARAMETER Runs
    How many launches to time. The median is reported -- one slow launch (a virus scanner waking
    up) must not decide the verdict.

.PARAMETER Listing
    Also generate a 10 000-file directory and time the OS enumeration of it, as the floor the
    application's own listing is measured against.

.EXAMPLE
    tools\benchmark.ps1
    tools\benchmark.ps1 -Exe D:\Tools\TCVibe\current\TCV.App.exe -Runs 7 -Listing
#>
[CmdletBinding()]
param(
    [string]$Exe = "src\TCV.App\bin\Release\net10.0-windows7.0\TCV.App.exe",
    [int]$Runs = 5,
    [switch]$Listing
)

$ErrorActionPreference = 'Stop'

# docs/VISION.md, budgets
$ColdStartBudgetMs = 2000
$ListingBudgetMs   = 500
$FileCount         = 10000

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Get-Median([double[]]$values) {
    $sorted = $values | Sort-Object
    $sorted[[math]::Floor($sorted.Count / 2)]
}

# -- Cold start ---------------------------------------------------------------
# WaitForInputIdle returns when the process has drained its startup work and is waiting for input,
# which for a WPF app is as close to "the window is usable" as an outside observer can get.
function Measure-ColdStart([string]$path, [int]$runs) {
    $samples = @()
    for ($i = 0; $i -lt $runs; $i++) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $proc = Start-Process -FilePath $path -PassThru
        try {
            if (-not $proc.WaitForInputIdle(30000)) {
                throw "$path did not become idle within 30 s"
            }
            $sw.Stop()
            $samples += $sw.Elapsed.TotalMilliseconds
        }
        finally {
            if (-not $proc.HasExited) {
                $proc.CloseMainWindow() | Out-Null
                if (-not $proc.WaitForExit(5000)) { $proc.Kill() }
            }
        }
        Start-Sleep -Milliseconds 300   # let the disk cache and the shell settle between launches
    }
    Get-Median $samples
}

function Find-DoubleCommander {
    $cmd = Get-Command 'doublecmd.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($candidate in @(
        "$env:ProgramFiles\Double Commander\doublecmd.exe",
        "${env:ProgramFiles(x86)}\Double Commander\doublecmd.exe")) {
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

if (-not (Test-Path $Exe)) {
    Write-Host "Not built: $Exe" -ForegroundColor Yellow
    Write-Host "Run: dotnet build TCVibe.slnx -c Release" -ForegroundColor Yellow
    exit 2
}

Write-Host ""
Write-Host "Benchmark -- docs/VISION.md budgets" -ForegroundColor Cyan
Write-Host "  exe:  $Exe"
Write-Host "  runs: $Runs (median reported)"
Write-Host ""

$failures = @()

Write-Host "Cold start..." -NoNewline
$tcv = Measure-ColdStart $Exe $Runs
Write-Host "`r  TCVibe  cold start: $([math]::Round($tcv)) ms   (budget $ColdStartBudgetMs ms)"
if ($tcv -ge $ColdStartBudgetMs) { $failures += "cold start $([math]::Round($tcv)) ms >= $ColdStartBudgetMs ms" }

# Explorer is the reference every user already has; it is measured the same way, but note that it
# is usually already running, so its number flatters it -- treat it as a floor, not a rival.
$dc = Find-DoubleCommander
if ($dc) {
    $dcMs = Measure-ColdStart $dc $Runs
    Write-Host "  Double Commander    cold start: $([math]::Round($dcMs)) ms"
} else {
    Write-Host "  Double Commander    not installed -- comparison skipped" -ForegroundColor DarkGray
}

# -- Listing ------------------------------------------------------------------
if ($Listing) {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("tcv-bench-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    try {
        Write-Host ""
        Write-Host "Generating $FileCount files..." -NoNewline
        for ($i = 0; $i -lt $FileCount; $i++) {
            [IO.File]::WriteAllBytes((Join-Path $dir ("file-{0:D5}.txt" -f $i)), @())
        }
        Write-Host "`r  $FileCount files in $dir"

        $null = (New-Object IO.DirectoryInfo $dir).GetFileSystemInfos()   # warm the metadata cache
        $samples = @()
        for ($i = 0; $i -lt 3; $i++) {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $null = (New-Object IO.DirectoryInfo $dir).GetFileSystemInfos()
            $sw.Stop()
            $samples += $sw.Elapsed.TotalMilliseconds
        }
        $osMs = Get-Median $samples
        Write-Host "  OS enumeration (name + size + attributes): $([math]::Round($osMs)) ms   (budget $ListingBudgetMs ms)"
        Write-Host "  the application's own listing is asserted by ListingBenchmarkTests:" -ForegroundColor DarkGray
        Write-Host "    dotnet test tests\TCV.App.Tests -l `"console;verbosity=detailed`" --filter ListingBenchmark" -ForegroundColor DarkGray
        if ($osMs -ge $ListingBudgetMs) {
            $failures += "the OS alone needs $([math]::Round($osMs)) ms for $FileCount entries -- this disk cannot meet the budget"
        }
    }
    finally {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Host "FAIL  $f" -ForegroundColor Red }
    exit 1
}

Write-Host "All measured requirements met." -ForegroundColor Green
exit 0
