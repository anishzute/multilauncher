Drag rows to reorder apps in both lists, or reorder profiles in the sidebar. Order is saved immediately, with an insertion indicator and scrolling while dragging. App rows and the running-app picker now show native executable or shortcut icons.

Closing defaults to **Exit completely**: try a normal window close, then terminate and verify any remaining tray or background processes. Bundled child executables are included when they belong to the app's process family and installation folder. Process ID, creation time, executable identity, Windows-session boundaries, and preserved pre-session apps are still checked. **Graceful only** remains available per app.

Existing profiles keep their paths, enabled apps, and order. The old unchecked Force close default now adopts Exit completely; save work in selected apps before starting or ending a session. Errors include the process ID and actionable permission details. Delayed launches can be tracked when the session ends; unidentified apps require explicit manual cleanup confirmation instead of a false success message.

The Windows verification workflow checks real disposable processes that hide on window close, windowless background processes, bundled helpers, and session restoration, plus UI startup and native icon extraction. These fixtures model the reported failures; Trading Paints, iRacing UI, and Plex themselves are not installed on the CI runner.

Download **Pitstop-Windows-x64.zip**, extract it, and run **Pitstop.exe**. Your profiles remain in `%LocalAppData%\Pitstop`. End any active session and exit the previous Pitstop version before replacing it.
