param(
    [string]$ProcessName = "",
    [string]$TitleLike = "",
    [string]$OutFile = "shot.png",
    [switch]$NoActivate
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Capture {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$proc = $null
if ($ProcessName) {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}
if (-not $proc -and $TitleLike) {
    $proc = Get-Process | Where-Object {
        $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$TitleLike*"
    } | Select-Object -First 1
}
if (-not $proc) { throw "No window found for ProcessName='$ProcessName' TitleLike='$TitleLike'" }

$h = $proc.MainWindowHandle
if (-not $NoActivate) {
    [Win32Capture]::ShowWindow($h, 9) | Out-Null
    [Win32Capture]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 350
}

# Prefer DWM extended frame bounds (excludes invisible resize borders)
$r = New-Object Win32Capture+RECT
$ok = $false
try {
    $hr = [Win32Capture]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
    if ($hr -eq 0) { $ok = $true }
} catch { }
if (-not $ok) {
    [Win32Capture]::GetWindowRect($h, [ref]$r) | Out-Null
}

$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { throw "Bad window rect" }

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$g.Dispose()

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

[pscustomobject]@{ Process = $proc.ProcessName; Title = $proc.MainWindowTitle; Rect = "$($r.Left),$($r.Top) ${w}x${ht}"; File = (Resolve-Path $OutFile).Path } | Format-List
