param(
    [Parameter(Mandatory)]
    [string]$Executable,
    [Parameter(Mandatory)]
    [string]$DataRoot,
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [string]$Version = '1.0.2',
    [ValidateSet('schedule', 'main', 'settings', 'program', 'about')]
    [string]$View = 'schedule'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ScheduleCaptureNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);
}
'@

function Save-WindowScreen([IntPtr]$Handle, [string]$Path) {
    $rect = New-Object ScheduleCaptureNative+RECT
    if (-not [ScheduleCaptureNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [Drawing.Size]::new($width, $height))
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

function Save-WindowPrint([IntPtr]$Handle, [string]$Path) {
    $rect = New-Object ScheduleCaptureNative+RECT
    if (-not [ScheduleCaptureNative]::GetWindowRect($Handle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }

    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $hdc = $graphics.GetHdc()
            try {
                if (-not [ScheduleCaptureNative]::PrintWindow($Handle, $hdc, 2)) {
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

function Find-ByAutomationId(
    [Windows.Automation.AutomationElement]$Root,
    [string]$AutomationId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ByName(
    [Windows.Automation.AutomationElement]$Root,
    [string]$Name) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-ProcessWindow([int]$ProcessId) {
    $processCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $windowCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Window)
    $condition = [Windows.Automation.AndCondition]::new($processCondition, $windowCondition)
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [Windows.Automation.TreeScope]::Children,
        $condition)
}

$originalDataRoot = $env:ESLEE_AUTOPOWER_DATA_ROOT
$originalNoStartup = $env:ESLEE_AUTOPOWER_NO_STARTUP
$env:ESLEE_AUTOPOWER_DATA_ROOT = (Resolve-Path $DataRoot).Path
$env:ESLEE_AUTOPOWER_NO_STARTUP = '1'
$process = $null
try {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $previewArgument = if ($View -eq 'program') {
        '--ui-preview-program'
    } elseif ($View -eq 'about') {
        '--ui-preview-about'
    } elseif ($View -in @('main', 'settings')) {
        '--ui-preview-main'
    } else {
        '--ui-preview-schedule'
    }
    $process = Start-Process -FilePath $Executable -ArgumentList $previewArgument -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $root = Get-ProcessWindow $process.Id
    } while ($null -eq $root -and [DateTime]::UtcNow -lt $deadline)

    if ($null -eq $root) {
        throw "The $View window did not appear."
    }

    $handle = [IntPtr]$root.Current.NativeWindowHandle
    [ScheduleCaptureNative]::ShowWindow($handle, 9) | Out-Null
    [ScheduleCaptureNative]::BringWindowToTop($handle) | Out-Null
    [ScheduleCaptureNative]::SetForegroundWindow($handle) | Out-Null
    $windowActivator = New-Object -ComObject WScript.Shell
    $windowActivator.AppActivate($process.Id) | Out-Null
    $root.SetFocus()
    [ScheduleCaptureNative]::BringWindowToTop($handle) | Out-Null
    [ScheduleCaptureNative]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Seconds 2

    if ($View -eq 'main') {
        Save-WindowPrint $handle (Join-Path $OutputDirectory "s3-s4-power-buttons-$Version.png")
        return
    }

    if ($View -eq 'settings') {
        $settingsButton = Find-ByAutomationId $root 'SettingsNavigationButton'
        if ($null -eq $settingsButton) {
            throw 'The settings navigation button was not found.'
        }
        $settingsButton.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 1
        Save-WindowPrint $handle (Join-Path $OutputDirectory "login-credential-settings-$Version.png")
        return
    }

    if ($View -eq 'program') {
        $advanced = Find-ByAutomationId $root 'AdvancedOptions'
        if ($null -eq $advanced) {
            throw 'The advanced options expander was not found.'
        }
        $advanced.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        [ScheduleCaptureNative]::BringWindowToTop($handle) | Out-Null
        [ScheduleCaptureNative]::SetForegroundWindow($handle) | Out-Null
        Start-Sleep -Seconds 1
        Save-WindowPrint $handle (Join-Path $OutputDirectory "follow-up-program-advanced-$Version.png")
        return
    }

    if ($View -eq 'about') {
        Save-WindowPrint $handle (Join-Path $OutputDirectory "about-$Version.png")
        return
    }

    Save-WindowPrint $handle (Join-Path $OutputDirectory "unified-auto-start-editor-$Version.png")

    $autoLogon = Find-ByAutomationId $root 'OneTimeAutoLogonCheck'
    if ($null -eq $autoLogon) {
        throw 'The one-time auto-logon checkbox was not found.'
    }
    $toggle = $autoLogon.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) {
        throw 'The one-time auto-logon checkbox is not enabled by default for a new schedule.'
    }

    $actionInput = Find-ByAutomationId $root 'ActionInput'
    if ($null -eq $actionInput) {
        throw 'ActionInput was not found.'
    }
    $actionInput.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 600
    Save-WindowScreen $handle (Join-Path $OutputDirectory "unified-auto-start-actions-$Version.png")

    $actionInput.SetFocus()
    [Windows.Forms.SendKeys]::SendWait('{DOWN}{ENTER}')
    Start-Sleep -Seconds 1

    $oneHour = Find-ByAutomationId $root 'ShutdownAfterOneHourButton'
    $twoHours = Find-ByAutomationId $root 'ShutdownAfterTwoHoursButton'
    if ($null -eq $oneHour -or $null -eq $twoHours) {
        throw 'The one-hour or two-hour shutdown shortcut was not found.'
    }
    Save-WindowPrint $handle (Join-Path $OutputDirectory "shutdown-shortcuts-$Version.png")

    $oneHour.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    $timeInput = Find-ByAutomationId $root 'TimeInput'
    if ($null -eq $timeInput) {
        throw 'TimeInput was not found after using the quick shutdown shortcut.'
    }
    $actualQuickTime = $timeInput.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    $expectedQuickTime = [DateTime]::Now.AddHours(1).ToString('HH:mm')
    if ($actualQuickTime -ne $expectedQuickTime) {
        throw "The one-hour shortcut produced '$actualQuickTime'; expected '$expectedQuickTime'."
    }

    $actionInput.SetFocus()
    [Windows.Forms.SendKeys]::SendWait('{HOME}')
    Start-Sleep -Milliseconds 500

    $wakeModeInput = Find-ByAutomationId $root 'WakeModeInput'
    if ($null -eq $wakeModeInput) {
        throw 'WakeModeInput was not found.'
    }
    $wakeModeInput.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 600
    Save-WindowScreen $handle (Join-Path $OutputDirectory "unified-auto-start-modes-$Version.png")
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }

    $env:ESLEE_AUTOPOWER_DATA_ROOT = $originalDataRoot
    $env:ESLEE_AUTOPOWER_NO_STARTUP = $originalNoStartup
}
