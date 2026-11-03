# Validation for v0.2.0

Development checks cross-compile the Windows app and test fixtures from Linux with the .NET 10 SDK. The Windows verification workflow separately executes native process control, WMI inventory, UI startup, and shell icon extraction on a Windows x64 runner before a tagged preview is published. See the Actions run and the build report included in the ZIP for the exact execution result.

The implementation passed all 37 core tests and all six native checks in [Windows verification run 41717496483](https://github.com/anishzute/multilauncher/actions/runs/41717496483), including portable packaging and checksum generation.

## Executed checks

The platform-independent suite now has **37 tests**. The original 20 scenarios exercise:

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

## Native Windows verification

The workflow uses isolated disposable apps to check:

1. Actual WPF window, resources, row templates, and tray-icon initialization.
2. Native executable icon extraction.
3. A real app that hides on `WM_CLOSE`: graceful-only leaves it alive, complete exit terminates it.
4. Real windowless background-process termination and verification.
5. Full session flow: close a background fixture, start a tray fixture, exit it, and restore the original background app.
6. Bundled child cleanup while preserving a child outside the installation folder.

The fixtures live in a temporary directory and never target installed user apps. The workflow builds the portable app, includes its Windows x64 runtime and dependency notices, and creates checksummed artifacts. A release is created only after verification succeeds.

Trading Paints, iRacing UI, and Plex themselves are not installed on the runner. The fixtures reproduce the relevant tray/background/helper behavior; the exact app versions, launcher setup, and permissions still need a check on the target PC. Windows services and other sessions remain excluded.

## Recommended first Windows check

Exit the previous Pitstop before replacing it. If the old version cannot finish restoring, exit from its tray menu and choose No to keep the saved session; use Retry restore after opening v0.2.0. Profiles remain in LocalAppData. Use two ordinary disposable apps with no unsaved work for a first check:

1. Open app A; leave app B closed.
2. Create a test profile with A in the Close list and B in the Open list. Use Exit completely, the new default.
3. Start the session. Confirm A exits and B opens.
4. End the session. Confirm B exits and A reopens.
5. Open B first and repeat. Confirm B stays open after ending the session.
6. Start again, close Pitstop to the tray, reopen it, and end the session.
7. From the tray, exit while keeping the session. Reopen Pitstop and verify the saved restore action.
8. Drag rows to the top, middle, and bottom of both lists, including a scroll while dragging; reopen Pitstop to confirm persistence. Reorder sidebar profiles too, and verify app rows cannot switch lists.
9. Confirm app/shortcut icons, app picker, import/export, and layout at your monitor’s scaling.

For Trading Paints, iRacing UI, and Plex, confirm the actual process path using Pick a running app and check Task Manager rather than just the taskbar. Exit completely should remove tray/background processes. Access-denied failures include the PID and permission guidance. Apps already running on the Open list are intentionally preserved and labeled accordingly. Select Graceful only explicitly when you want a refusing app left alive.
