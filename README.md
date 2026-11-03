# Pitstop

A small native Windows app for switching between desktop setups. Keep a profile for iRacing, Assetto Corsa Competizione, Flight Simulator, work, or anything else. Start the profile to open its tools and close the background apps you selected. End the session to close session apps and reopen your previous apps.

## Download

[Download Pitstop for Windows x64](https://github.com/anishzute/multilauncher/raw/refs/heads/main/builds/v0.1.0/Pitstop-Windows-x64.zip).

The [v0.1.0 build folder](builds/v0.1.0) also contains the original source archive, SHA-256 checksums, and validation report. This preview passed 20 session-engine tests and cross-compiles for Windows; the Windows interface and actual process control still need a Windows smoke test.

## Run the portable app

1. Extract `Pitstop-Windows-x64.zip` to a folder on your Windows 10/11 x64 PC.
2. Double-click `Pitstop.exe`. No installer, Python, or separate .NET installation is required.
3. Select a starter profile. Click **Edit** next to each app and choose its executable or Windows shortcut. **Pick a running app** can find apps that are open already.
4. Turn on the apps you want to include. All starter entries are off until you configure them; installation paths differ between PCs.
5. Add unwanted background apps to **Close while you play**. Give each one a valid launch target so it can be reopened.
6. Click **Start session**. When done, click **End & restore**.

The binary is an unsigned first version. Source and build instructions are included separately. See `VALIDATION.md` for what was checked and what still needs a Windows smoke test.

## Included profiles

- **iRacing:** Trading Paints, RaceLab, OpenKneeboard, SimHub, Marvin’s AIRA, Virtual Desktop Streamer, and iRacing UI.
- **Assetto Corsa Competizione:** SimHub, OpenKneeboard, Virtual Desktop Streamer, and ACC.
- **Flight Simulator:** OpenKneeboard, Virtual Desktop Streamer, and Flight Simulator.

Plex Media Server and Samsung Notes are disabled suggestions on each Close list. Add any other apps you want to manage. Pitstop does not guess which processes are unnecessary.

Create, rename, duplicate, or delete profiles. Duplicating a configured profile is an easy way to share tools across different games. Click an app’s Edit button to move it up or down in the launch order, remove it, set arguments, or add a delay after launching it.

## Close behavior and restoration

- Before making changes, Pitstop saves a session record to disk.
- Close-list apps are asked to exit before open-list apps launch.
- An app already running on the Open list is kept running and stays open after the session.
- Apps launched for the session are tracked by executable path, process ID, and creation time. Another app with the same filename in another folder does not match. Reused process IDs do not match.
- Only close-list apps that were running when the session started are candidates for reopening. If an app is already running at restore time, it is not launched again.
- Where Windows provides it, the original launch arguments are saved for a directly launched executable. A configured shortcut or launch link remains the restart route when one is used.
- Graceful exit is the default. A **Force close** option is available per app. It also applies when ending a session for apps on the Open list. Background apps without windows, including many tray utilities, may need this option. Force close can lose unsaved work.
- If a close or restore cannot finish, the record stays on disk. Resolve the item in Session activity and use **Retry restore**. Finished restore steps are not repeated.
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

Pitstop waits up to 10 seconds to detect a launched process. If a launcher takes longer, Session activity reports that the process was not detected. Such an untracked game must be exited manually. List helper apps separately if they use a different executable and should be closed too.

## Local data and backups

All data is under `%LocalAppData%\Pitstop`:

| File | Purpose |
| --- | --- |
| `profiles.json` | Your profiles and app settings |
| `active-session.json` | Current session and unfinished restore steps |
| `history\*.json` | Completed session records and action logs |
| `*.bak` | Previous version of a replaced data file |

Writes use a flushed temporary file and atomic replacement. A damaged JSON file is preserved and reported; it is not silently replaced with a fresh session. If necessary, close Pitstop, preserve the damaged file, and review its `.bak` copy. Do not delete a session record while you still need its restore information.

Settings & help offers profile export/import and a button to open the data folder. Import adds profiles rather than replacing yours. Review paths and Force close settings after import. Session records may include the command-line arguments of apps selected for closing; treat these local files as private. Pitstop has no network client, cloud account, or telemetry. History can be removed from the data folder when you no longer need it.

## Build from source

Install the **.NET 10 SDK**. On Windows, open PowerShell in this folder:

```powershell
./Build.ps1
```

This runs the session tests and creates a portable x64 build at `dist/Pitstop-win-x64.zip`. To target Windows on ARM, use `./Build.ps1 -Runtime win-arm64`; that target has not been validated in this build.

For development:

```powershell
dotnet run --project tests/Pitstop.Tests -c Release
dotnet run --project src/Pitstop.Windows
```

The UI is WPF. The core session engine is independent of Windows process control so its behavior can be tested without touching real apps. `WindowsProcessHost` uses WMI for inventory and the Windows process APIs for launching and exiting apps. It never kills by filename or recursively terminates an arbitrary process tree.
