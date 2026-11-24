# DT Local Studio 0.2.0
Windows WPF application. Version 0.1.0 startup and UI response were verified on the target Windows PC. Version 0.2.0 adds on-demand Zoom main-window scanning and a separate DWM preview process. Build and live preview of this revision require Windows verification.

## Build
Run build_windows.bat, then publish\win-x64\DTLocalStudio.exe. Close old studio and preview windows before rebuilding.

## Test
1. Launch with Zoom closed. Test UI responsiveness and scan; no Zoom source should be found.
2. Open Zoom and join an authorized meeting. Restore the meeting window and scan. Select the relevant listed main window.
3. Open live preview. Check motion and shared content. Resize preview, close it and reopen it.
4. Close Zoom; preview should report unavailable. Studio remains responsive. Rejoin, scan and reopen.
5. Close studio; preview should close. Check startup logs at %LOCALAPPDATA%\DTLocalStudio\Logs\startup.log.

Expected milestones: FIRST_RENDER, UI_RESPONSE, ZOOM_SCAN, PREVIEW_REQUEST, PREVIEW_REGISTERED, PREVIEW_CLOSED. DWM success only indicates thumbnail registration/update, not actual frame quality.

## Limitations
DWM preview is display-only, not access to raw video frames. One main window per Zoom-prefixed process; some meeting/share windows may not be listed. Minimized, protected or GPU-rendered windows may appear blank or stale. Keep Zoom restored. No audio capture, cropping, recording, background removal or composition yet. No OBS or Zoom SDK dependency. Native preview calls occur in the worker only; scan uses managed Process APIs. Windows Graphics Capture and audio/encoding are later milestones. Only capture authorized meetings.
