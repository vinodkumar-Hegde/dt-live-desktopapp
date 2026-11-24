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

## 0.5.0 assisted studio
Auto refresh Zoom main windows every 3 seconds. Open Recording Studio once; frame preview starts automatically. Suggested regions are aspect-ratio heuristics, not person detection or semantic sharing detection. Confirm both nonoverlapping regions once; saved layout can be restored only for the same source dimensions and must be reconfirmed each session. Arm automatic recording explicitly; starts after three seconds of system audio above -40 dBFS and valid frames, in window-render mode only. Stop/save disarms. Capture error or stall finalizes; silence never auto-pauses because lecture pauses are ambiguous. Source size change invalidates confirmation. 720p 15 fps output target, actual measured capture rate displayed; repeated frames are possible. Background GDI composition, UI preview limited to 5 updates/sec, CPU x264 ultrafast preset. Not Windows Graphics Capture, hardware encoding, or real faculty recognition yet. Windows runtime tests required; validate playback, bitrate and sync. System audio may contain unrelated applications. Do not force-kill while recording.

## 0.6.0
Window scan uses a separate worker and all visible top-level Zoom windows, ranks meeting titles/classes and area. Auto-open checkbox is opt-in; automatically opens a likely meeting and arms recording. Layout confirmation still required, as crops are not reliably identified. AUTO_STATE diagnostics show blockers. No real faculty recognition or GPU capture yet. Windows test pending.

## 0.6.1
Replaces fixed automatic crop presets with experimental luminance-component and temporal-motion pane detection. Looks anywhere within the captured window for a large rectangular content region and smaller separate moving rectangle. Ambiguity or absence of visible gutters/motion blocks automatic recording; no face identity model or Zoom sharing metadata. Three consistent observations required. Automatically armed studio starts after three seconds of valid frames with a stable detected layout and available system audio device; continuous loud speech no longer required. Manual draw/confirm is fallback. Fixed bottom-right is output composition only. No runtime verification yet. Keep Zoom restored in side-by-side sharing view. Do not use with unauthorized sessions.

## 0.6.2
Resolve FFmpeg to an absolute executable before recording, using saved path, application tools, current and persisted Windows PATH, WinGet links and package directories. Validate libx264 and AAC in a bounded background process. Log FFMPEG_READY or FFMPEG_ERROR; automatic recording waits for validation. Detect FFmpeg button retries. Windows runtime verification pending.

## 0.6.4 audio continuity
Preserve sample order instead of correcting every packet against callback arrival time. A bounded audio queue moves PCM disk writes off the WASAPI callback; substantial gaps (over 250 ms) and recording/pause boundaries are handled separately. Apply 0.85 gain for headroom; AAC 128 kbps. AUDIO_SUMMARY logs packet count, gap/padding durations and input clipping. This reduces a plausible source of crackle, but actual device glitches and long-duration sync still require testing. System output audio remains non-isolated.
