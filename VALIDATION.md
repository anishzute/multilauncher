# Validation for v0.4.0

Development checks cross-compile the Windows app and test fixtures from Linux with the .NET 10 SDK. The Windows verification workflow separately executes native process control, WMI inventory, UI startup, and shell icon extraction on a Windows x64 runner before a tagged preview is published. See the Actions run and the build report included in the ZIP for the exact execution result.

The v0.4.0 suite has **52 core tests** and **nine Windows checks**. Development checks verify cancellation, abandoned-session recovery, and persistent JSON before the native Windows workflow runs.

All 52 core tests, all nine Windows checks, and portable packaging passed in [Windows verification run 42125256279](https://github.com/anishzute/multilauncher/actions/runs/42125256279).

## Executed checks

The platform-independent suite has **52 tests**. The original 20 scenarios exercise:

1. Full start/end flow, preserving already-running tools and original launch arguments.
2. Reused process IDs.
3. Close failures, durable recovery records, and retry without duplicate restores.
4. Restart failures and retries.
5. Recovery after recreating the session engine from the saved record.
6. Disabled rules.
7. Conflicts between Open and Close lists.
8. Invalid launch targets before any close requests.
9. Empty profiles.
10. Rejection of a second active session.
11. Preservation of independently opened later app instances.
12. Closure of owned child instances with the same executable path.
13. Persistence of intent before process effects.
14. Profile persistence, atomic writes, backups, and completed-session archives.
15. Fail-closed behavior for corrupt session files.
16. Restoration after a failed game launch.
17. Background apps that restart themselves.
18. Preservation of per-app Force close consent in the session snapshot.
19. Recovery of an interrupted launch from path and creation time.
20. Exact, case-insensitive executable path matching.

Seventeen further scenarios cover hiding to the tray, windowless processes, graceful-only mode, normal exit, already-exited processes, denied/ineffective termination, old-profile migration, complete exit in both session directions, bundled and external children, exited bootstrappers, reused parent PIDs, delayed launches, unidentified-launch resolution, failed launches followed by manual instances, and false close-success reports.

Fifteen new scenarios cover resetting a failed restore without process actions; starting a new profile after reset; cancellation during inventory, an issued close, and a long launch delay; stopping and resuming; terminal reset records; archive failure; preserving profiles and order through reset and app-folder replacement; missing, damaged, and legacy backup recovery; refusing to overwrite unrecoverable profiles with starters; intentionally empty profile lists; and a failed backup destination that must not prevent primary JSON persistence.

## Native Windows verification

The workflow uses isolated disposable apps to check:

1. Actual WPF window, resources, row templates, and tray-icon initialization.
2. Native executable icon extraction.
3. A real app that hides on `WM_CLOSE`: graceful-only leaves it alive, complete exit terminates it.
4. Real windowless background-process termination and verification.
5. Full session flow: close a background fixture, start a tray fixture, exit it, and restore the original background app.
6. Bundled child cleanup while preserving a child outside the installation folder.
7. A failed-restore window exposes Reset and Exit; Keep session & exit closes the actual UI without retrying or discarding profiles.
8. A real failed restore is reset without changing processes, and its profiles persist when the app reopens.
9. The latest profile JSON is recovered from backup on Windows after the main file is removed.

The fixtures live in a temporary directory and never target installed user apps. The workflow builds the portable app, includes its Windows x64 runtime and dependency notices, and creates checksummed artifacts. A release is created only after verification succeeds.

Trading Paints, iRacing UI, and Plex themselves are not installed on the runner. The fixtures reproduce the relevant tray/background/helper behavior; the exact app versions, launcher setup, and permissions still need a check on the target PC. Windows services and other sessions remain excluded.

## Recommended first Windows check

Exit the previous Loadout before replacing it. If the old version cannot finish restoring, exit from its tray menu and choose No to keep the saved session; open v0.3.0 and choose Reset session or Retry restore. Profiles remain in LocalAppData. Use two ordinary disposable apps with no unsaved work for a first check:

In v0.3.0 you can use Reset session instead of retrying. Reset leaves apps as they are and keeps profiles. Exit offers explicit keep/reset choices. Check Settings for the exact profiles.json path and automatic backup path before updating or switching Windows accounts.

1. Open app A; leave app B closed.
2. Create a test profile with A in the Close list and B in the Open list. Use Exit completely, the new default.
3. Start the session. Confirm A exits and B opens.
4. End the session. Confirm B exits and A reopens.
5. Open B first and repeat. Confirm B stays open after ending the session.
6. Start again, close Loadout to the tray, reopen it, and end the session.
7. From the tray, exit while keeping the session. Reopen Loadout and verify the saved restore action.
8. Drag rows to the top, middle, and bottom of both lists, including a scroll while dragging; reopen Loadout to confirm persistence. Reorder sidebar profiles too, and verify app rows cannot switch lists.
9. Confirm app/shortcut icons, app picker, import/export, and layout at your monitor’s scaling.

For Trading Paints, iRacing UI, and Plex, confirm the actual process path using Pick a running app and check Task Manager rather than just the taskbar. Exit completely should remove tray/background processes. Access-denied failures include the PID and permission guidance. Apps already running on the Open list are intentionally preserved and labeled accordingly. Select Graceful only explicitly when you want a refusing app left alive.
