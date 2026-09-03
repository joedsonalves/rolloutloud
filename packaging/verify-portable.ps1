<#
    Reproduces the situation of somebody who downloads the executable.

    This is the only check that catches the failure it is written for, and the reason is that the
    bug is INVISIBLE from the build folder. Avalonia carries native libraries (Skia, HarfBuzz,
    ANGLE); if they extract beside the executable instead of into it, running from the publish
    directory works perfectly and copying the single file anywhere else kills the process before
    a window appears. Testing where you built it proves nothing.

    So: copy the exe alone into an empty folder somewhere else, run it there, and require that a
    window actually opens (GUI) or that it exits 0 (CLI).

    Usage:
      powershell -ExecutionPolicy Bypass -File packaging\verify-portable.ps1 `
          -PublishDir artifacts\gui -Executable RolloutLoud.exe -ExpectWindow

      powershell -ExecutionPolicy Bypass -File packaging\verify-portable.ps1 `
          -PublishDir artifacts\cli -Executable rollout.exe -Arguments help
#>
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$Executable,
    [string[]]$Arguments = @(),
    [switch]$ExpectWindow,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

$source = Join-Path (Resolve-Path $PublishDir) $Executable
if (-not (Test-Path $source)) { throw "not found: $source" }

# Somewhere with nothing else in it, and NOT under the repository — a stray dependency sitting
# next to the build output is exactly what this is looking for.
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("rolloutloud-portable-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

try {
    Copy-Item $source $sandbox -Force
    $target = Join-Path $sandbox $Executable
    Write-Output "Running $Executable from $sandbox (the file, alone)"

    # Splatted, and ArgumentList omitted entirely when there are none: Start-Process rejects an
    # empty array with "the argument is null, empty, or an element ... contains a null value",
    # which reads like a bug in the executable rather than in the call.
    $start = @{ FilePath = $target; PassThru = $true }
    if ($Arguments.Count -gt 0) { $start.ArgumentList = $Arguments }

    if ($ExpectWindow) {
        $proc = Start-Process @start
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $handle = [IntPtr]::Zero

        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 400
            $proc.Refresh()

            if ($proc.HasExited) {
                # 0xC000041D is the one to recognise: a fail-fast raised inside the window
                # procedure, which escapes every managed handler. It almost always means a native
                # library did not come along for the ride.
                $exit = $proc.ExitCode
                $hex = [Convert]::ToString($exit, 16).PadLeft(8, '0')
                $hint = 'If that is c000041d, the native libraries were not embedded - check IncludeNativeLibrariesForSelfExtract.'
                throw "FAILED: exited with $exit (0x$hex) before showing a window. $hint"
            }

            if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
                $handle = $proc.MainWindowHandle
                break
            }
        }

        if ($handle -eq [IntPtr]::Zero) {
            try { $proc.Kill() } catch { }
            throw "FAILED: no window within $TimeoutSeconds s."
        }

        Write-Output "  OK — window appeared"
        try { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null } catch { }
    }
    else {
        $start.NoNewWindow = $true; $start.Wait = $true
        $proc = Start-Process @start
        if ($proc.ExitCode -ne 0) {
            $exit = $proc.ExitCode
            $hex = [Convert]::ToString($exit, 16).PadLeft(8, '0')
            throw "FAILED: exit code $exit (0x$hex)."
        }

        Write-Output "  OK — exited 0"
    }
}
finally {
    # The app writes .rolloutloud/ into its working directory, so the sandbox is not empty by the
    # time we are done with it.
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
