<#
    Publishes the portable executables that go into a GitHub release.

    IncludeNativeLibrariesForSelfExtract is the setting that matters, and it is not optional.
    Avalonia carries native libraries (Skia, HarfBuzz, ANGLE). Without this flag they are
    extracted BESIDE the executable rather than into it: running from the publish folder works
    perfectly, and copying the single file anywhere else kills the process before the window
    appears. Vacuon hit exactly this with WPF's five native DLLs and a 0xC000041D that escapes
    every exception handler because it is a fail-fast inside the window procedure.

    verify-portable.ps1 exists to reproduce the situation of somebody who downloads the file, and
    it should be run against what this produces, every release, without exception.

    Usage:  powershell -ExecutionPolicy Bypass -File packaging\publish.ps1 [-Version 0.1.0]
#>
param(
    [string]$Version = '',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$artifacts = Join-Path $root 'artifacts'

if (-not $Version) {
    $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    if ($props -match '<Version>([^<]+)</Version>') { $Version = $Matches[1] }
    else { throw 'No <Version> in Directory.Build.props and none passed.' }
}

Write-Output "Publishing RolloutLoud $Version for $Runtime"

# The GUI locks its own DLLs while running, and the copy step fails — sometimes silently, which
# is worse, because then a release is cut from stale binaries.
Get-Process RolloutLoud -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output "  killing running RolloutLoud (pid $($_.Id))"
    try { $_.Kill() } catch { throw "RolloutLoud is running and could not be killed. If it is elevated, close the window by hand." }
    $_.WaitForExit(5000) | Out-Null
}

$targets = @(
    @{ Project = 'src\RolloutLoud.App'; Out = 'gui'; Exe = 'RolloutLoud.exe' },
    @{ Project = 'src\RolloutLoud.Cli'; Out = 'cli'; Exe = 'rollout.exe' }
)

foreach ($t in $targets) {
    $outDir = Join-Path $artifacts $t.Out
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

    dotnet publish (Join-Path $root $t.Project) `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:Version=$Version `
        -o $outDir `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "publish failed for $($t.Project)" }

    $exe = Join-Path $outDir $t.Exe
    if (-not (Test-Path $exe)) { throw "expected $exe to exist after publish" }

    $size = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Output "  $($t.Exe)  $size MB"
}

# Release assets carry two names each, for two different readers.
#
#   RolloutLoud.exe            — for people. Windows search shows a loose executable by its file
#                                name, and a version-stamped one is not what anybody types.
#   RolloutLoud-<v>-win-x64.exe — for winget, which pins a SHA256 against a stable URL.
#
# The CLI cannot be called rolloutloud.exe: it would collide with the GUI on a case-insensitive
# filesystem, which is why it is `rollout` everywhere.
$release = Join-Path $artifacts 'release'
New-Item -ItemType Directory -Force -Path $release | Out-Null

Copy-Item (Join-Path $artifacts 'gui\RolloutLoud.exe') (Join-Path $release 'RolloutLoud.exe') -Force
Copy-Item (Join-Path $artifacts 'cli\rollout.exe') (Join-Path $release 'rollout-cli.exe') -Force
Copy-Item (Join-Path $artifacts 'gui\RolloutLoud.exe') (Join-Path $release "RolloutLoud-$Version-$Runtime.exe") -Force
Copy-Item (Join-Path $artifacts 'cli\rollout.exe') (Join-Path $release "rollout-$Version-$Runtime.exe") -Force

Write-Output ''
Write-Output 'SHA256 of the release assets:'
Get-ChildItem $release -Filter *.exe | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    Write-Output ("  {0,-38} {1}" -f $_.Name, $hash)
}

Write-Output ''
Write-Output 'Next: packaging\verify-portable.ps1 — it runs these from a folder they were not built in.'
Write-Output 'The SHA256 that goes into the manifest is the one of the asset DOWNLOADED FROM THE RELEASE,'
Write-Output 'not the one above: winget fetches the published URL, and re-uploading can change bytes.'
