# AGENT.md

## Project Snapshot
- **Name:** bigfoot
- **Type:** WPF desktop overlay app (.NET 8, Windows)
- **Goal:** Visualize left/right directional audio cues (e.g., footsteps in FPS games) from system output audio using NAudio loopback capture.
- **Main package:** `NAudio` (`bigfoot.csproj`)

## Core Architecture
- **Capture + DSP:** `AudioMonitor.cs`
  - Captures default render output via `WasapiLoopbackCapture`.
  - Reads stereo samples (float/16-bit/24-bit/32-bit PCM).
  - Optional band weighting (~120 Hz high-pass + ~3500 Hz low-pass).
  - Computes per-window RMS + peak blended metrics.
  - Uses mid/side metrics for directionality.
  - Optional "Exclude Myself" gate based on absolute side RMS threshold.
  - Applies attack/release smoothing envelope.
  - Emits `(left, right)` via `LevelCalculated` event.

- **Overlay UI + Interaction:** `MainWindow.xaml` + `MainWindow.xaml.cs`
  - Transparent, topmost, click-through overlay with a 9-bar indicator.
  - Tray icon context menu:
    - Exclude Myself
    - 7-point quantized position
    - Frequency weighting
    - Theme (Default / Red / Black / White)
    - Exit
  - Left-click tray icon opens quick settings (threshold + vertical position sliders).
  - UI animation timer at ~60 FPS updates bar scales, opacity, and horizontal position.

- **Settings Persistence:** `AppSettingsStore.cs`
  - JSON in `%AppData%\bigfoot\settings.json`.
  - Stored values:
    - `SilenceThreshold`
    - `VerticalPositionRatio`
    - `ExcludeMyself`
    - `UseQuantizedPosition`
    - `UseFrequencyWeighting`
    - `ColorTheme`

## Startup + Lifecycle
1. `MainWindow` loads settings.
2. Initializes overlay and tray UX.
3. Starts `AudioMonitor` and subscribes to `LevelCalculated`.
4. UI timer consumes target ratio/loudness under `_audioLock`.
5. On close: unsubscribes events, disposes capture/tray resources.

## Build & Run
- Build: `dotnet build`
- Run: `dotnet run`
- Target framework: `net8.0-windows`

## Session Guidelines (for future agents)
1. **Preserve overlay behavior**
   - Keep window extended styles (transparent/toolwindow/noactivate).
   - Do not break click-through + topmost semantics.

2. **Respect audio thread/UI thread boundaries**
   - Keep `OnLevelCalculated` lightweight.
   - Use `_audioLock` for shared target state.
   - Avoid expensive allocations in tight loops.

3. **Tune carefully**
   - Thresholds and smoothing directly affect responsiveness/flicker.
   - Keep defaults stable unless explicitly asked.
   - If changing filter/gate values, note rationale and expected gameplay effect.

4. **Maintain settings compatibility**
   - Add new settings with safe defaults.
   - Avoid breaking deserialization of existing JSON.

5. **Keep tray UX consistent**
   - Ensure menu checked states reflect real runtime state.
   - Unsubscribe handlers and dispose UI resources on shutdown.

## Important Runtime Constants/Behaviors
- Analysis window: ~16ms
- Envelope smoothing: attack 0.55, release 0.20
- RMS/Peak blend: 70% / 30%
- Silence opacity gate uses `_silenceThreshold`
- Position mapping:
  - Quantized mode: 7-point horizontal slots
  - Smooth mode: eased continuous movement

## Quick Manual Verification Checklist
- App launches with no visible main window chrome.
- Tray icon appears and context menu works.
- Left-click tray icon opens quick settings popup.
- Changing threshold/vertical position updates behavior and persists after restart.
- Exclude Myself, quantized mode, and frequency weighting toggles apply immediately.
- Theme switch changes bar colors and persists.
- Overlay tracks loud left/right audio direction and fades when quiet.

## Suggested Backlog Ideas
- Device selection instead of only the default output device.
- Calibration wizard for threshold and weighting per game/headset.
- Debug overlay mode showing mid/side metrics numerically.
- Optional history trail or smoothing presets.
- Multi-monitor target selection.
