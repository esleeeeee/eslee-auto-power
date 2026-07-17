param(
    [Parameter(Mandatory)]
    [string]$Executable,
    [Parameter(Mandatory)]
    [string]$DataRoot,
    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CompatibilityCaptureNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);
}
'@

function Save-WindowCapture([IntPtr]$Handle, [string]$Path) {
    $rect = New-Object CompatibilityCaptureNative+RECT
    if (-not [CompatibilityCaptureNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $hdc = $graphics.GetHdc()
            try {
                if (-not [CompatibilityCaptureNative]::PrintWindow($Handle, $hdc, 2)) {
                    throw 'PrintWindow failed.'
                }
            }
            finally {
                $graphics.ReleaseHdc($hdc)
            }
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

$originalDataRoot = $env:ESLEE_AUTOPOWER_DATA_ROOT
$originalNoStartup = $env:ESLEE_AUTOPOWER_NO_STARTUP
$env:ESLEE_AUTOPOWER_DATA_ROOT = (Resolve-Path $DataRoot).Path
$env:ESLEE_AUTOPOWER_NO_STARTUP = '1'
$process = $null
try {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $process = Start-Process -FilePath $Executable -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)

    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The WPF main window did not appear.'
    }

    $root = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $automationIdCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        'CompatibilityNavigationButton')
    $button = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $automationIdCondition)
    $buttonDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ($null -eq $button -and [DateTime]::UtcNow -lt $buttonDeadline) {
        $button = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $automationIdCondition)
        if ($null -eq $button) {
            Start-Sleep -Milliseconds 250
        }
    }
    if ($null -eq $button) {
        $buttonCondition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button)
        $names = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $buttonCondition) |
            ForEach-Object { $_.Current.Name }
        throw "The compatibility navigation button was not found. Buttons: $($names -join ', ')"
    }

    $pattern = $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Seconds 1
    $windowActivator = New-Object -ComObject WScript.Shell

    $sizes = @(
        @{ Name = 'compatibility-min-900x700.png'; Width = 900; Height = 700 },
        @{ Name = 'compatibility-default-1000x700.png'; Width = 1000; Height = 700 },
        @{ Name = 'compatibility-wide-1200x700.png'; Width = 1200; Height = 700 }
    )
    $dpiScale = [CompatibilityCaptureNative]::GetDpiForWindow($process.MainWindowHandle) / 96.0
    foreach ($size in $sizes) {
        $physicalWidth = [int][Math]::Round($size.Width * $dpiScale)
        $physicalHeight = [int][Math]::Round($size.Height * $dpiScale)
        [CompatibilityCaptureNative]::SetWindowPos(
            $process.MainWindowHandle,
            [IntPtr]::Zero,
            80,
            60,
            $physicalWidth,
            $physicalHeight,
            0) | Out-Null
        [CompatibilityCaptureNative]::ShowWindow($process.MainWindowHandle, 9) | Out-Null
        [CompatibilityCaptureNative]::BringWindowToTop($process.MainWindowHandle) | Out-Null
        [CompatibilityCaptureNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
        $windowActivator.AppActivate($process.Id) | Out-Null
        Start-Sleep -Seconds 2
        Save-WindowCapture $process.MainWindowHandle (Join-Path $OutputDirectory $size.Name)
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }

    $env:ESLEE_AUTOPOWER_DATA_ROOT = $originalDataRoot
    $env:ESLEE_AUTOPOWER_NO_STARTUP = $originalNoStartup
}
