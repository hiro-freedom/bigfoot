# AGENT.md

## Project Snapshot
- **Name:** bigfoot
- **Type:** WPF desktop overlay app (.NET 8, Windows)
- **Goal:** Accurately identify the direction of footsteps in FPS games.
- **Main package:** `NAudio` (`bigfoot.csproj`)

## Core Architecture
- **Capture + DSP:** `AudioMonitor.cs`
  - Captures default render output via `WasapiLoopbackCapture`.
  - Reads stereo samples (float/16-bit/24-bit/32-bit PCM).
  - Optional band weighting (~120 Hz high-pass + ~3500 Hz low-pass).
  - Computes per-window RMS + peak blended metrics.
  - Uses mid/side metrics for directionality.
  - Optional "Exclude Myself" hybrid soft gate (relative directionality + absolute side floor + gate envelope).
  - Applies attack/release smoothing envelope.
  - Emits `(left, right)` via `LevelCalculated` event.

- **Overlay UI + Interaction:** `MainWindow.xaml` + `MainWindow.xaml.cs`
  - Transparent, topmost, click-through overlay with a 9-bar indicator.
  - Tray icon context menu:
    - Exclude Myself
    - 7-point quantized position
    - Frequency weighting
    - Frequency Band Analysis...
    - Theme (Default / Red / Black / White)
    - Exit
  - Left-click tray icon opens quick settings (threshold + vertical position sliders).
  - UI animation timer at ~60 FPS updates bar scales, opacity, and horizontal position.

- **Offline Analysis Tool:** `FrequencyBandAnalysisWindow.xaml` + `FrequencyBandAnalysisWindow.xaml.cs`
  - WAV file loading, playback controls, and timeline seek.
  - Segment annotation on waveform (`Footstep`, `Ambience`, `Mixed`).
  - PSD-based offline analysis over labeled segments.
  - Contrast curve (`Footstep / Ambience`) + confidence-aware recommended band output.
  - Save/load recommendation history in JSON (`%AppData%\bigfoot\analysis-recommendations`).

- **Settings Persistence:** `AppSettingsStore.cs`
  - JSON in `%AppData%\bigfoot\settings.json`.
  - Stored values:
    - `SilenceThreshold`
    - `VerticalPositionRatio`
    - `ExcludeMyself`
    - `UseQuantizedPosition`
    - `UseFrequencyWeighting`
    - `ColorTheme`
    - `AnalysisLastProfileName`
    - `AnalysisLastRecommendationDirectory`
    - `AnalysisLastRecommendationFilePath`

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

6. **Keep AGENT.md in English**
   - All descriptions in `AGENT.md` must be written in English.

7. **Keep code comments in English**
   - All source-code comments must be written in English.

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

## Last 5 changes
- Improved `Exclude Myself` using a hybrid soft gate (relative + absolute side floor) with gate envelope smoothing to reduce missed quiet footsteps.
- Added tray entry `Frequency Band Analysis...` and integrated a dedicated analysis window.
- Implemented offline WAV workflow: playback, timeline seek, and waveform preview.
- Implemented segment annotation workflow (`Footstep` / `Ambience` / `Mixed`) with list management.
- Implemented PSD-based contrast analysis with confidence-aware recommended `HighPassHz` / `LowPassHz`.

## Current Phase Goal
- **Goal:** Implement an offline frequency-band analysis workflow to tune `HighPassHz` / `LowPassHz` per game profile.
- **Status:** Core workflow implemented (iterations 1-6 complete), now in validation and tuning phase.
- **Scope boundary:** Keep analysis fully offline/read-only and isolated from the real-time `AudioMonitor` path until recommendations are validated.

### Approved workflow design (current baseline)
1. **Entry point (tray UX)**
   - Add a tray context-menu item: `Frequency Band Analysis...`.
   - Clicking it opens a separate analysis tool window.

2. **Input model**
   - Use pre-recorded local audio files (WAV first, optional MP3 support later).
   - Do not read from live capture in this phase.

3. **Annotation model (segment-based, not single-point)**
   - Users select short time ranges (segments), not individual timestamps.
   - Segment labels: `Footstep`, `Ambience`, `Mixed`.
   - Recommended segment duration: ~200-600 ms.
   - Recommended minimum sample count per label: 5-10 segments.

4. **Analysis model**
   - Compute spectrogram/PSD over labeled segments.
   - Aggregate spectra by label.
   - Compare `Footstep` vs `Ambience` to derive a frequency-advantage curve.
   - Output a recommended band (`HighPassHz`, `LowPassHz`) plus confidence and notes.

5. **Output and persistence**
   - Generate a per-game recommendation sheet (JSON or equivalent structured output).
   - Keep recommendation application manual in this phase (no automatic runtime DSP mutation).

6. **Validation loop**
   - Validate recommendations with quick in-game A/B runs.
   - Iterate per game/headset/map style before promoting defaults.

### Why this design is preferred
- Segment-level aggregation is more stable than single-point analysis for transient/noisy FPS audio.
- `Footstep` vs `Ambience` comparison is more actionable than raw spectral values alone.
- Keeping the tool offline/read-only minimizes regression risk for overlay responsiveness and real-time audio behavior.

## Later Optimization Backlog (Prioritized)
1. **P0 - Adaptive noise-floor logic (high impact, low coupling)**
   - Compute recent baseline noise and adapt activation thresholds per scene.
   - Goal: preserve distant footstep sensitivity while reducing false triggers.

2. **P1 - Event-level footstep detection before direction mapping**
   - Detect transient footstep-like attack/decay events first, then estimate left/right direction.
   - Goal: prevent continuous ambience/voice from driving direction output.
   - Dependency: works best after P0 baseline/noise adaptation is in place.

3. **P1 - Multi-band scoring instead of single full-band metrics**
   - Split weighted audio into multiple sub-bands and fuse directional evidence.
   - Goal: improve robustness when non-footstep sounds overlap the main footstep band.
   - Dependency: pair with event-level gating for cleaner per-band evidence.

4. **P2 - Game/profile presets for tuning and deployment**
   - Persist separate parameter presets per game, headset, or map style.
   - Goal: avoid one-size-fits-all tuning and speed up calibration.
   - Dependency: should consume validated outputs from P0/P1 tuning.

5. **P2 - Frequency-band analysis workflow expansion (still offline/read-only)**
   - Purpose: derive profile-specific high-pass/low-pass ranges beyond fixed 120-3500 Hz.
   - Plan: record representative samples, analyze PSD/spectrogram, produce recommendation sheets, validate with quick A/B runs.
   - Dependency: integrates naturally with profile presets once recommendations are stable.

6. **P3 - Optional advanced DSP feature set (non-ML first)**
   - Candidate features: spectral flux, inter-band energy ratios, zero-crossing rate, short-term autocorrelation.
   - Goal: build a lightweight score/classifier for `footstep` vs `non-footstep` gating.
   - Dependency: evaluate after P0-P2 establish a stable baseline.

7. **P4 - Optional ML path after DSP baseline stabilizes**
   - Export labeled clips and benchmark a small ONNX model for `footstep/non-footstep`.
   - Goal: enable model-assisted gating only if accuracy gain is material over non-ML DSP.
   - Dependency: requires stable labels/features and profile workflow from earlier phases.
