# Validation for v0.1

This version was built in a Linux environment with the .NET 10 SDK. The native Windows interface, WMI access, system tray behavior, launcher handoffs, and actual Windows app closing were **not executed here**. The executable is a working Windows-targeted build, with Windows runtime verification still required.

## Executed checks

The platform-independent session tests exercise a simulated process host rather than real desktop processes. The suite checks:

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

The WPF application is compiled, including XAML and embedded icon resources. The portable publish includes its Windows x64 .NET runtime. `build-report.txt` in the download records build and test output.

## Recommended first Windows check

Use two ordinary, disposable apps with no unsaved work before configuring your real gaming profile:

1. Open app A; leave app B closed.
2. Create a test profile with A in the Close list and B in the Open list. Keep Force close off to begin with.
3. Start the session. Confirm A exits and B opens.
4. End the session. Confirm B exits and A reopens.
5. Open B first and repeat. Confirm B stays open after ending the session.
6. Start again, close Pitstop to the tray, reopen it, and end the session.
7. From the tray, exit while keeping the session. Reopen Pitstop and verify the saved restore action.
8. Confirm the app UI, app picker, import/export, and launch order work at your monitor’s normal scaling.

Test each game’s actual launcher route and each background app’s close behavior separately. A background app that ignores graceful exit remains running and is reported in activity; enable its Force close setting only if that behavior is appropriate for that app.
