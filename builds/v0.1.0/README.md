# Pitstop v0.1.0 preview

[Download the portable Windows x64 app](https://github.com/anishzute/multilauncher/raw/refs/heads/main/builds/v0.1.0/Pitstop-Windows-x64.zip).

Extract the ZIP and run `Pitstop.exe` on Windows 10/11 x64. No separate .NET installation is required. Configure each app’s path before enabling the starter entries.

| File | Contents |
| --- | --- |
| `Pitstop-Windows-x64.zip` | Self-contained Windows app, instructions, and dependency notices |
| `Pitstop-Source.zip` | Original source snapshot used for this build |
| `SHA256SUMS.txt` | Checksums for both ZIP archives |
| `build-report.txt` | Build details, executable checksum, and test results |
| `VALIDATION.md` | Checked behavior and recommended Windows smoke tests |

Both archive checksums and portable ZIP integrity were verified before upload. The source in the original build archive matches the app’s implementation. The 20 session tests were rerun successfully on November 3, 2026.

This preview was built on Linux for Windows x64. The Windows interface, system tray, WMI inventory, and actual app launching/closing have not been exercised on Windows here. Restoration reopens apps; it cannot reconstruct unsaved work, exact window layouts, or in-memory game state.
