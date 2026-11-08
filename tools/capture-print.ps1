param(
    [string]$ProcessName = "ILauncher",
    [string]$OutFile = "shot.png"
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class PrintCapture {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { throw "No window found for $ProcessName" }

$h = $proc.MainWindowHandle
$r = New-Object PrintCapture+RECT
try {
    $hr = [PrintCapture]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
    if ($hr -ne 0) { [PrintCapture]::GetWindowRect($h, [ref]$r) | Out-Null }
} catch {
    [PrintCapture]::GetWindowRect($h, [ref]$r) | Out-Null
}

$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { throw "Bad window rect" }

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
try {
    $ok = [PrintCapture]::PrintWindow($h, $hdc, 2)
} finally {
    $g.ReleaseHdc($hdc)
    $g.Dispose()
}

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

[pscustomobject]@{ Process = $proc.ProcessName; Title = $proc.MainWindowTitle; Rect = "${w}x${ht}"; PrintWindow = $ok; File = (Resolve-Path $OutFile).Path } | Format-List
