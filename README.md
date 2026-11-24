# DT Local Studio 0.1.0
Fresh WPF startup foundation. No previous project code, capture, Zoom SDK, FFmpeg or custom native interop. Source-only; not built or launched on Windows by the author.

## Build on Windows
Install .NET 10 SDK (or a compatible newer SDK with .NET 10 targeting support). Run `build_windows.bat` from PowerShell. Launch `publish\win-x64\DTLocalStudio.exe`.

## Acceptance
1. Window appears; click Test UI responsiveness.
2. Minimize and restore, resize, leave open for five minutes, and close normally.
3. Repeat five launches. Confirm FIRST_RENDER, UI_RESPONSE and EXIT code=0 in the log for each test.
4. Check Windows Application event log for new crashes.

Log: `%LOCALAPPDATA%\DTLocalStudio\Logs\startup.log`.

## Next stages (not implemented)
Windows Graphics Capture source picker and preview; audio capture and activity detection; FFmpeg MP4 with recovery; layout; background segmentation. Do not enable capture initialization during UI startup. Use a separate worker process for encoding and isolate capture failures. Capture only authorized sessions. Zoom remote faculty video and presentation may need separate windows or user-selected regions. Process-specific audio requires supported Windows builds; endpoint loopback may capture other apps.
