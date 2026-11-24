# DT Local Studio 0.3.0
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

## 0.3.0 capture lab
Close all studio processes before publishing. Scan Zoom and select a source, then click Capture lab. Start frames; window rendering is experimental and may return black or stale frames. Visible screen mode captures actual visible pixels, including overlapping windows; move lab aside and keep Zoom restored. Freeze, select Presentation or Faculty, drag over the source, then resume. Start audio meter to monitor default system output (not isolated Zoom audio); no files are saved. Frame counts and sampled motion are diagnostic, not proof of reliable video or faculty detection. Target 5 preview frames/sec, not production recording. Worker errors are isolated; reopen manually after source changes. Build and runtime verification on Windows pending. Production Windows Graphics Capture, process-only audio, recording and automation remain unimplemented.

## 0.4.0 manual recording (experimental)
Install FFmpeg with libx264 (e.g. winget install --id Gyan.FFmpeg -e), reopen PowerShell, verify ffmpeg -version. In capture lab choose both crops, Done / live, choose output folder, Start recording. Pause/resume, Stop and save MP4. Output 1280x720 at 5 fps, white canvas, separate presentation/faculty, system loopback audio (not Zoom-only). CPU libx264 1500 kbps target, AAC 96 kbps; not a hard file-size guarantee. Temporary MKV and PCM files are stored in a .work directory and removed only after successful MP4 mux. Audio alignment is approximate and must be tested. Do not force-kill during recording. Source capture still experimental; not production 30 fps. Windows build and recording verification pending.
