# eslee Auto Power

> A Windows 11 desktop utility for scheduled wake from S3 sleep or S4 hibernation, scheduled full shutdown/hibernation/sleep, and follow-up programs after resume.

[Download the latest English installer](https://github.com/esleeeeee/eslee-auto-power/releases/latest) · [한국어 설치 파일](https://github.com/esleeeeee/eslee-auto-power/releases/latest)

Documentation: [한국어](README.md) · **English**

## What does it solve?

A Windows `WakeToRun` task alone does not guide the user into the correct power state or handle the lock screen and follow-up programs. eslee Auto Power combines that flow into one schedule.

- Choose a wake time and Automatic, S3, or S4 mode.
- Put the PC into the matching power state from the main screen.
- At the scheduled time, Windows wakes the session and the app waits for the desktop-ready point `T0`.
- Follow-up programs run at `T0 + N minutes`.
- Optional one-time sign-in handling skips the lock screen for the next resume and restores the original setting afterward.
- A separate power-action schedule can enter full shutdown, hibernation, or sleep at a chosen time.

## Supported power states

| Power state | Scheduled wake | Scheduled transition | Notes |
|---|---:|---:|---|
| S3 sleep | Supported | Supported | Requires S3 support from Windows and the PC firmware. |
| S4 hibernation | Supported | Supported | Requires Windows hibernation to be enabled. |
| S5 full shutdown | Not supported | Supported | The app can schedule turning the PC off, but cannot turn it back on from a full shutdown. |

App-controlled startup after a full shutdown is intentionally not presented or emulated. For scheduled wake, leave the PC in **S3 sleep or S4 hibernation**.

## Quick start

1. Download `eslee-auto-power-v1.0.4-en-setup.exe` from [Releases](https://github.com/esleeeeee/eslee-auto-power/releases/latest).
2. Open Compatibility and run the two-minute real test for an available S3/S4 path.
3. In Settings, save and validate the Windows account password. Windows Hello PINs are not supported.
4. Create a scheduled wake or choose full shutdown, hibernation, or sleep. A new schedule starts with the local date and time at which the editor was opened.
5. Use `Enter S3 sleep now` or `Enter S4 hibernation now` with the same power state as the schedule.

Save unsaved work before entering sleep or hibernation.

## Quick power-transition schedules

Selecting full shutdown, hibernation, or sleep in the new-schedule editor shows the same `In 1 hour` and `In 2 hours` quick controls. They stay hidden for scheduled wake.

- The calculation starts from the current local time at the exact moment the button is clicked.
- Midnight, month-end, year-end, and leap-year boundaries are handled by the Windows date calculation.
- Seconds and milliseconds are normalized to zero, and the date and time remain manually editable.
- Changing the selected action does not reset the date or time. Clicking a quick control again recalculates from the new click time.
- The controls only populate the existing date and time fields, so validation and saving use the normal path.

## Scheduled full shutdown

Full shutdown is the S5 action that turns the PC off. It is separate from waking or booting a PC from S5.

- A warning appears exactly five minutes before the action.
- At the scheduled time, the app uses the documented Windows `InitiateShutdownW` API to request a planned S5 full shutdown with a 30-second grace period. It supports unattended shutdown with other signed-in sessions and does not use hybrid/Fast Startup shutdown.
- A rejected primary request no longer finalizes the schedule or deletes its watchdog. A separate SYSTEM fallback makes an immediate forced request after 30 seconds, and the schedule becomes failed only if that fallback is also rejected.
- If a later scheduled wake is active, the app explains that full shutdown prevents it from turning the PC back on. The five-minute warning can switch to the S3/S4 state required by that wake or continue with full shutdown after confirmation.

Hibernation is recommended for a long wait and sleep for a short wait when a later scheduled wake is needed.

## How it works

```mermaid
flowchart LR
    A["Save schedule"] --> B["Register Windows WakeToRun task"]
    B --> C["Enter S3 sleep or S4 hibernation"]
    C --> D["Windows wakes at the scheduled time"]
    D --> E["Confirm user session and Explorer are ready"]
    E --> F["Set T0"]
    F --> G["Launch follow-up programs at T0 + N"]
```

Before a scheduled sleep, hibernation, or full shutdown, the app durably stores `ExecutionStarted`, a pending journal, and `PendingPowerTransition`, then consumes the one-time task. Full shutdown distinguishes primary accepted/rejected and fallback pending/invoked/accepted/rejected in that journal. After resume or on the next app start, it reconciles Windows power events with Task Scheduler evidence and records completed, failed, missed, or unknown. A past schedule is never assumed successful or run late merely because its time has passed.

The application separates responsibilities into three processes:

- `AutoPower.App.exe`: standard-user WPF UI, tray, schedules, history, and settings
- `AutoPower.Helper.exe`: user-approved elevated power transitions and app-owned task registration
- `AutoPower.Agent.exe`: resume readiness and follow-up program execution in the user session

Task Scheduler changes are limited to the app-owned `\eslee\AutoPower\` folder. Tasks owned by other software are not modified.

## App-managed one-time sign-in

Windows may require a password after S3/S4 resume. When a schedule enables app-managed one-time sign-in, the app temporarily disables the Windows `require sign-in on wake` value for the next resume only.

- The Windows account password is stored in Credential Manager and an app-specific LSA protected secret.
- A Microsoft account email is normalized automatically.
- Windows Hello PINs are never stored or used.
- Original AC/DC sign-in requirement values are restored and verified after resume.
- If a process is interrupted, journal-based recovery retries during the next wake task, app start, sign-in, or uninstall.

If the PC is woken manually before the schedule, that resume may also open without a lock screen. Use this option only on a physically secure PC.

## Follow-up programs and elevation

Programs run relative to the actual desktop-ready time `T0`, not the planned wake time. Multiple programs may use the same delay, and one failure does not stop the rest.

For a program that requires elevation, open Advanced options and select `Run as administrator`. The app pre-registers a highest-privilege task while saving the schedule, so the resumed desktop does not wait for a UAC prompt. Use this only for trusted programs.

## Compatibility and limitations

- Operating system: Windows 11 x64
- S3 requires firmware support for that state.
- S4 requires both firmware support and Windows hibernation.
- The app never enables hibernation without consent. When needed, it explains how to run `powercfg /hibernate on` in an elevated terminal.
- Actual results may depend on UEFI power policy, Windows wake-timer settings, and vendor firmware.
- Release installers are currently unsigned, so Windows SmartScreen may display a warning.

Compatibility is not marked as confirmed from capability detection alone. Only a successful real S3/S4 wake test records `Confirmed supported`; the same screen also reports whether app-managed one-time sign-in credentials have been validated.

## Local data and privacy

Schedules, execution history, recovery journals, and diagnostics are stored under `%ProgramData%\eslee\AutoPower`. Passwords are never stored in the plain-text database or logs; Windows-protected storage is used. The app contains no analytics, advertising, or external telemetry transport.

Diagnostic logs may include local details needed for troubleshooting, such as task results, error codes, and executable paths. Review them before attaching them to a public issue. See [Privacy](PRIVACY.md) for details.

## Building from source

Requirements:

- Windows 11 x64
- .NET SDK 10.0.301
- Inno Setup 6 for installers

```powershell
dotnet restore .\AutoPower.sln
dotnet build .\AutoPower.sln -c Release
dotnet test .\tests\AutoPower.Tests\AutoPower.Tests.csproj -c Release
.\scripts\Build-Release.ps1 -Version 1.0.4
```

Use `-p:AppLanguage=ko` or `-p:AppLanguage=en` for a single-language build. The release script produces self-contained x64 Korean and English installers plus SHA-256 files under `artifacts\installer`.

## Repository layout

```text
src/AutoPower.App       WPF UI and tray
src/AutoPower.Core      Models, policies, validation, localization
src/AutoPower.Data      SQLite storage
src/AutoPower.Windows   Task Scheduler, power, credentials, recovery
src/AutoPower.Helper    Elevated command runner
src/AutoPower.Agent     Resume and follow-up program handling
tests/AutoPower.Tests   Automated tests
installer               Inno Setup definition
```

## More information

- [Changelog](CHANGELOG.md)
- [Security policy](SECURITY.md)
- [Privacy](PRIVACY.md)
- [GitHub Issues](https://github.com/esleeeeee/eslee-auto-power/issues)

## License

[MIT License](LICENSE)
