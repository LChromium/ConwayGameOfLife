<#
.SYNOPSIS
    Captures the client area of a real Player window, for interface review.

.DESCRIPTION
    The interface is reviewed from screenshots of the actual Windows Player, at the sizes
    the review names (1600x900 and 600x1000). Those captures have to be reproducible, so
    this script is kept with the project rather than retyped each round.

    Two details that a naive capture gets wrong, and that are handled here:

      * DPI. The process is made DPI aware, so GetClientRect and ClientToScreen return
        physical pixels on a scaled desktop instead of silently scaled ones.
      * The client area, not the window rect. Capturing the window rect includes the DWM
        frame and a strip of whatever is behind it; the previous round's screenshots had
        desktop pixels along the right and bottom edges for exactly that reason.

    The Player is launched with -screen-width/-screen-height, so the client area is the
    requested size and no window resizing is needed.

.EXAMPLE
    pwsh Tools\capture-player-window.ps1 -Width 1600 -Height 900 `
        -Arguments '-lifeBoard 256x256 -lifeZoom 1 -lifeSeedPreview -lifeSeed 20260915 ``
                   -lifeDensity 0.32 -lifeScale 40 -lifeWarp 8 -lifeCluster 0.7' `
        -Output Screenshots\player-seed-ui-1600x900.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$Width,
    [Parameter(Mandatory = $true)][int]$Height,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Arguments = '',
    [string]$Player = 'Builds\LifeTerminal.exe',
    [int]$SettleSeconds = 4,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinCap {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

[void][WinCap]::SetProcessDPIAware()

$playerPath = (Resolve-Path $Player).Path
$commandLine = "-screen-width $Width -screen-height $Height -screen-fullscreen 0 $Arguments"
Write-Host "launching: $playerPath $commandLine"

$process = Start-Process -FilePath $playerPath -ArgumentList $commandLine -PassThru

try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "the Player exited with code $($process.ExitCode) before a window appeared" }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and [WinCap]::IsWindowVisible($process.MainWindowHandle)) { break }
    }

    $handle = $process.MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw "no main window after $TimeoutSeconds seconds" }

    # The preview is generated on a background thread; the settle time is for it to land.
    [void][WinCap]::SetForegroundWindow($handle)
    Start-Sleep -Seconds $SettleSeconds

    $rect = New-Object WinCap+RECT
    if (-not [WinCap]::GetClientRect($handle, [ref]$rect)) { throw 'GetClientRect failed' }

    $origin = New-Object WinCap+POINT
    if (-not [WinCap]::ClientToScreen($handle, [ref]$origin)) { throw 'ClientToScreen failed' }

    $captureWidth = $rect.Right - $rect.Left
    $captureHeight = $rect.Bottom - $rect.Top
    Write-Host "client area: ${captureWidth}x${captureHeight} at $($origin.X),$($origin.Y)"

    $bitmap = New-Object System.Drawing.Bitmap $captureWidth, $captureHeight
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($origin.X, $origin.Y, 0, 0, $bitmap.Size)

    $outputPath = if ([System.IO.Path]::IsPathRooted($Output)) {
        [System.IO.Path]::GetFullPath($Output)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Output))
    }
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($outputPath)) | Out-Null
    $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)

    $graphics.Dispose()
    $bitmap.Dispose()

    Write-Host "saved $outputPath ($((Get-Item $outputPath).Length) bytes)"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
    }
}
