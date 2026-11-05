# Pitstop

A small native Windows app for switching between desktop setups. Keep a profile for iRacing, Assetto Corsa Competizione, Flight Simulator, work, or anything else. Start the profile to open its tools and close the background apps you selected. End the session to close session apps and reopen your previous apps.

## Download

[Download Pitstop v0.3.0 for Windows x64](https://github.com/anishzute/multilauncher/releases/download/v0.3.0/Pitstop-Windows-x64.zip).

The [releases page](https://github.com/anishzute/multilauncher/releases) contains the portable build, source archive, and SHA-256 checksums. The original [v0.1.0 build folder](builds/v0.1.0) is retained for reference. The Windows verification workflow runs 52 core regression tests and nine native checks before publishing tagged preview releases.

## New in v0.3.0

**Reset session** abandons a failed restore without retrying it. During an operation, the button reads **Stop & reset**: pending steps are canceled, an already-issued app action is allowed to finish, and the session record is archived as abandoned. Apps are left as they are, profiles are kept, and you can edit or start another profile immediately. Reset is available in the session banner and tray menu.

The visible **Exit** button and tray Exit action offer **Keep session & exit** or **Reset & exit**. Neither choice retries restore. Keep preserves the record for a later restore; reset archives it and exits. Exit remains available while an operation is stopping.

Profiles auto-save to the fixed JSON file `%LocalAppData%\Pitstop\profiles.json`, outside the app folder. Settings shows the full path and offers Open profiles.json, Back up profiles, and Import backup. Replacing or moving the executable does not replace your profiles under the same Windows account.

Each successful profile save also updates `profiles.backup.json`; previous `.bak` copies are retained. If the main file is missing or damaged, Pitstop recovers a valid backup before creating any starter profiles. Damaged files are preserved for reference. Moving to a different Windows account requires exporting/importing the JSON backup.

## New in v0.2.0

Drag rows to change order in either app list or the profile sidebar. An insertion line shows the drop position, lists scroll while dragging, and the new order saves immediately. App rows stay within their original Open or Close list.

Configured apps and the running-app picker display native executable or shortcut icons. Unconfigured entries retain their initials.

Closing now defaults to **Exit completely**: request a normal exit, allow three seconds, then terminate and verify any remaining tray or background process. This corrects the old window-close default that could hide Trading Paints or leave Plex/iRacing UI running. Bundled child executables are included when they descend from the selected app and live in its installation folder. **Graceful only** remains an explicit per-app alternative.

Existing profiles keep their paths and order, and the old unchecked Force close default adopts Exit completely. Save work in selected apps before starting or ending a session. Exit the previous Pitstop from its tray menu before replacing it. If it cannot finish a session, choose No when asked to restore on exit to retain the record, then use Retry restore in v0.2.0. Profiles and session records remain in LocalAppData.

## Run the portable app

1. Extract `Pitstop-Windows-x64.zip` to a folder on your Windows 10/11 x64 PC.
2. Double-click `Pitstop.exe`. No installer, Python, or separate .NET installation is required.
3. Select a starter profile. Click **Edit** next to each app and choose its executable or Windows shortcut. **Pick a running app** can find apps that are open already.
4. Turn on the apps you want to include. All starter entries are off until you configure them; installation paths differ between PCs.
5. Add unwanted background apps to **Close while you play**. Give each one a valid launch target so it can be reopened.
6. Click **Start session**. When done, click **End & restore**.

The binary is an unsigned preview. Source and build instructions are included separately. See `VALIDATION.md` for automated verification and the checks to perform with your actual apps.

## Included profiles

- **iRacing:** Trading Paints, RaceLab, OpenKneeboard, SimHub, Marvin’s AIRA, Virtual Desktop Streamer, and iRacing UI.
- **Assetto Corsa Competizione:** SimHub, OpenKneeboard, Virtual Desktop Streamer, and ACC.
- **Flight Simulator:** OpenKneeboard, Virtual Desktop Streamer, and Flight Simulator.

Plex Media Server and Samsung Notes are disabled suggestions on each Close list. Add any other apps you want to manage. Pitstop does not guess which processes are unnecessary.

Create, rename, duplicate, or delete profiles. Duplicating a configured profile is an easy way to share tools across different games. Drag rows to reorder them; Edit also offers move buttons, removal, launch arguments, and delays.

## Close behavior and restoration

- Before making changes, Pitstop saves a session record to disk.
- Close-list apps are asked to exit before open-list apps launch.
- An app already running on the Open list is kept running and stays open after the session.
- Apps launched for the session are tracked by executable path, process ID, and creation time. Another app with the same filename in another folder does not match. Reused process IDs do not match.
- Only close-list apps that were running when the session started are candidates for reopening. If an app is already running at restore time, it is not launched again.
- Where Windows provides it, the original launch arguments are saved for a directly launched executable. A configured shortcut or launch link remains the restart route when one is used.
- **Exit completely** is the default, including for apps on the Open list when ending a session. It verifies process exit and terminates tray/background apps that refuse a normal window close. Save work first. Choose **Graceful only** per app to leave a refusing app running instead.
- Bundled helpers are included by default, restricted to the selected app’s process family and installation folder. Unrelated programs outside that folder and pre-session apps are preserved.
- Session activity shows PIDs and actual exit results. Access-denied messages explain when Pitstop may need to be restarted as administrator.
- Delayed launcher processes can be recovered when ending a session. If an app cannot be identified, the session remains saved and offers manual cleanup confirmation instead of reporting false success.
- If a close or restore cannot finish, the record stays on disk. You can Retry restore, Reset session without retrying, or Exit while keeping the record. Finished restore steps are not repeated.
- Closing Pitstop during a session minimizes it to the system tray. The tray menu can reopen Pitstop, end the session, or quit while keeping the saved session for later.
- The app allows one active session at a time. Profiles are locked while that session is active.

**Restoring means reopening apps.** Unsaved documents, exact window layouts, browser tabs, game state, and in-memory data are controlled by each app and cannot be reconstructed by Pitstop. Apps may use their own session recovery. Multiple processes belonging to one close-list executable reopen as one app, not as a reconstruction of every original worker process.

Pitstop manages ordinary apps in your current Windows session. It excludes Windows-directory programs, core Windows process names, Windows services, and itself. It does not disable services, change startup items, uninstall bloatware, or touch other users’ sessions. Elevated or protected apps may deny access; those failures are reported.

## Launchers, games, and Store apps

**Launch target** is what starts the app. **Process executable** is the exact `.exe` to track. They can be different.

- Normal executable: select the `.exe`; both paths usually match.
- Windows shortcut: select a `.lnk`. Pitstop tries to read its target to fill in the process path. Arguments already inside the shortcut should stay there.
- Steam game: a launch target such as `steam://rungameid/805550` can launch ACC. Choose the actual game executable as Process executable. Steam handles the launch normally.
- Other launcher: select its executable and enter its normal launch arguments. Set Process executable to the resulting game or app, not a shared launcher you want to keep running.
- Store app: an installed app may support a `shell:AppsFolder\PACKAGE!APP` launch target. Use the actual installed AppUserModelID, and choose the running app’s executable for tracking. Packaging or permissions may prevent some Store apps from being managed.

Pitstop checks for a launched process at startup and again when ending the session if a launcher was slow. If it still cannot identify the executable, Session activity asks you to check the process path and confirm manual cleanup. Bundled helpers in the same installation folder can be tracked automatically; add helper apps outside that folder separately if needed.

## Local data and backups

All data is under `%LocalAppData%\Pitstop`:

| File | Purpose |
| --- | --- |
| `profiles.json` | Your profiles and app settings, saved outside the app folder |
| `profiles.backup.json` | Automatic recovery copy of the latest saved profiles |
| `active-session.json` | Current session and unfinished restore steps |
| `history\*.json` | Completed session records and action logs |
| `*.bak` | Previous version of a replaced data file |

Writes use a flushed temporary file and atomic replacement. Missing or damaged profiles are recovered from a valid backup when possible, with the damaged original preserved. If all copies are unreadable, they are not overwritten by starter profiles. Active-session JSON is kept separate from profile JSON: resetting a session never deletes profiles or their backups. Abandoned sessions stay in history for reference, without being resumed on the next launch.

Settings & help offers profile export/import and a button to open the data folder. Import adds profiles rather than replacing yours. Review paths and close behavior after import. Session records may include the command-line arguments of apps selected for closing; treat these local files as private. Pitstop has no network client, cloud account, or telemetry. History can be removed from the data folder when you no longer need it.

## Build from source

Install the **.NET 10 SDK**. On Windows, open PowerShell in this folder:

```powershell
./Build.ps1
```

This runs the session tests and creates a portable x64 build at `dist/Pitstop-Windows-x64.zip`, including dependency licenses. To target Windows on ARM, use `./Build.ps1 -Runtime win-arm64`; ARM is not covered by the x64 verification workflow.

For development:

```powershell
dotnet run --project tests/Pitstop.Tests -c Release
dotnet run --project src/Pitstop.Windows
```

The UI is WPF. The core session engine is independent of Windows process control so its behavior can be tested without touching real apps. `WindowsProcessHost` uses WMI for inventory and limited-information process handles for identity verification and exit. It never kills by filename or recursively terminates an arbitrary process tree. The [Windows verification workflow](https://github.com/anishzute/multilauncher/actions/workflows/windows-verification.yml) exercises real disposable processes, UI startup, and icon extraction; tagged builds publish only after verification succeeds.
