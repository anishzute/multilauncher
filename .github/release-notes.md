Failed restores now have a clear escape: **Reset session** abandons pending restore steps, archives the record, and unlocks profiles without retrying or changing additional apps. During an operation, **Stop & reset** cancels the remaining steps and waits for any already-issued app action before resetting. Apps are left as they are; profiles are kept.

A visible **Exit** button and the tray menu offer **Keep session & exit** or **Reset & exit**. Neither choice retries restore, and exit remains available during an operation.

Profiles auto-save to `%LocalAppData%\Pitstop\profiles.json`, outside the application folder. Settings shows the exact path and offers Open profiles.json, Back up profiles, and Import backup. The latest saved profiles also have an automatic `profiles.backup.json` recovery copy. Missing or damaged files recover from valid backups; damaged originals are preserved. App updates, session resets, and changing the executable folder do not replace profile JSON under the same Windows account.

Verification covers **52 regression tests** and **nine native Windows checks**, including failed-restore exit UI, reset without process changes, and JSON recovery after reopening the app.

Download **Pitstop-Windows-x64.zip**, extract it, and run **Pitstop.exe**. Exit the older Pitstop from its tray menu before replacing it. If v0.2 cannot finish a session, choose **No** when asked to restore on exit to keep its record, then open v0.3 and choose **Reset session** or **Retry restore**. Existing profiles load automatically.
