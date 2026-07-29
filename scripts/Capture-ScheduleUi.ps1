param(
    [Parameter(Mandatory)]
    [string]$Executable,
    [Parameter(Mandatory)]
    [string]$DataRoot,
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [string]$Version = '1.0.4',
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

function Test-ElementVisible([Windows.Automation.AutomationElement]$Element) {
    if ($null -eq $Element) {
        return $false
    }

    $bounds = $Element.Current.BoundingRectangle
    return -not $Element.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0
}

function Select-Action(
    [Windows.Automation.AutomationElement]$ActionInput,
    [int]$Index) {
    $ActionInput.SetFocus()
    [Windows.Forms.SendKeys]::SendWait('{HOME}')
    for ($step = 0; $step -lt $Index; $step++) {
        [Windows.Forms.SendKeys]::SendWait('{DOWN}')
    }
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 500
}

function Get-SelectedActionName([Windows.Automation.AutomationElement]$ActionInput) {
    $selection = $ActionInput.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selection.Count -ne 1) {
        throw "Expected one selected action, found $($selection.Count)."
    }
    return $selection[0].Current.Name
}

function Assert-QuickPowerVisibility(
    [Windows.Automation.AutomationElement]$Root,
    [bool]$ExpectedVisible,
    [string]$Context) {
    $oneHour = Find-ByAutomationId $Root 'PowerAfterOneHourButton'
    $twoHours = Find-ByAutomationId $Root 'PowerAfterTwoHoursButton'
    $actual = (Test-ElementVisible $oneHour) -and (Test-ElementVisible $twoHours)
    if ($actual -ne $ExpectedVisible) {
        throw "Quick power visibility for $Context was $actual; expected $ExpectedVisible."
    }
    return @($oneHour, $twoHours)
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
    Assert-QuickPowerVisibility $root $false 'automatic start' | Out-Null

    $actionInput.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 600
    Save-WindowScreen $handle (Join-Path $OutputDirectory "unified-auto-start-actions-$Version.png")

    Select-Action $actionInput 1
    $shutdownButtons = Assert-QuickPowerVisibility $root $true 'full shutdown'
    $shutdownName = Get-SelectedActionName $actionInput
    $shutdownAccessibilityName = $shutdownButtons[0].Current.Name
    if ([string]::IsNullOrWhiteSpace($shutdownAccessibilityName) -or $shutdownAccessibilityName -notmatch '1') {
        throw "The one-hour accessibility name is not descriptive: '$shutdownAccessibilityName'."
    }
    Save-WindowPrint $handle (Join-Path $OutputDirectory "quick-power-shutdown-$Version.png")

    $beforeQuickClick = [DateTime]::Now
    $shutdownButtons[0].GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    $afterQuickClick = [DateTime]::Now
    $timeInput = Find-ByAutomationId $root 'TimeInput'
    $dateInput = Find-ByAutomationId $root 'DateInput'
    if ($null -eq $timeInput) {
        throw 'TimeInput was not found after using the quick power shortcut.'
    }
    if ($null -eq $dateInput) {
        throw 'DateInput was not found after using the quick power shortcut.'
    }
    if ((Get-SelectedActionName $actionInput) -ne $shutdownName) {
        throw 'Using the quick power shortcut changed the selected action.'
    }
    $actualQuickTime = $timeInput.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    $expectedQuickTimes = @(
        $beforeQuickClick.AddHours(1).ToString('HH:mm'),
        $afterQuickClick.AddHours(1).ToString('HH:mm')) | Select-Object -Unique
    if ($actualQuickTime -notin $expectedQuickTimes) {
        throw "The one-hour shortcut produced '$actualQuickTime'; expected one of '$($expectedQuickTimes -join ', ')'."
    }
    $quickDate = $dateInput.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    $quickTime = $actualQuickTime

    Select-Action $actionInput 2
    $hibernateButtons = Assert-QuickPowerVisibility $root $true 'hibernate'
    $hibernateAccessibilityName = $hibernateButtons[0].Current.Name
    if ([string]::IsNullOrWhiteSpace($hibernateAccessibilityName) -or
        $hibernateAccessibilityName -eq $shutdownAccessibilityName) {
        throw "The hibernation accessibility name did not change with the selected action: '$hibernateAccessibilityName'."
    }
    if ($dateInput.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $quickDate -or
        $timeInput.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $quickTime) {
        throw 'Changing from full shutdown to hibernation reset the quick date or time.'
    }
    Save-WindowPrint $handle (Join-Path $OutputDirectory "quick-power-hibernate-$Version.png")
    $hibernateName = Get-SelectedActionName $actionInput
    $hibernateButtons[0].GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    if ((Get-SelectedActionName $actionInput) -ne $hibernateName) {
        throw 'Using the hibernation quick shortcut changed the selected action.'
    }

    Select-Action $actionInput 3
    $sleepButtons = Assert-QuickPowerVisibility $root $true 'sleep'
    $sleepAccessibilityName = $sleepButtons[1].Current.Name
    if ([string]::IsNullOrWhiteSpace($sleepAccessibilityName) -or
        $sleepAccessibilityName -eq $hibernateAccessibilityName -or
        $sleepAccessibilityName -notmatch '2') {
        throw "The sleep accessibility name did not change with the selected action: '$sleepAccessibilityName'."
    }
    Save-WindowPrint $handle (Join-Path $OutputDirectory "quick-power-sleep-$Version.png")
    $sleepName = Get-SelectedActionName $actionInput
    $sleepButtons[1].GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    if ((Get-SelectedActionName $actionInput) -ne $sleepName) {
        throw 'Using the sleep quick shortcut changed the selected action.'
    }

    $timePattern = $timeInput.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $datePattern = $dateInput.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    if ($timePattern.Current.IsReadOnly -or $datePattern.Current.IsReadOnly) {
        throw 'Date or time became read-only after using a quick power shortcut.'
    }
    $timePattern.SetValue('13:37')
    if ($timePattern.Current.Value -ne '13:37') {
        throw 'Time could not be manually edited after using a quick power shortcut.'
    }

    Select-Action $actionInput 0
    Assert-QuickPowerVisibility $root $false 'automatic start after power actions' | Out-Null

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
