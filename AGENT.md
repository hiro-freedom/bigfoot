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
  - Optional band weighting (~90 Hz high-pass + ~1200 Hz low-pass 2nd-order Biquad Butterworth, tuned for low-end thump + mid-high suppression).
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

- **ML Training Workspace (offline):** `footstep-ml/`
  - Purpose: train/export ONNX models for AI-enhanced event prediction.
  - Current baseline model: multi-head CNN (`event_head`, `human_head`, `self_head`).
  - Event classes: `Footstep`, `Gunshot`, `Other`.
  - Trigger intent: detect human-related audio (`footstep or gunshot`) while suppressing self-generated sounds when labels are available.

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
- **Goal:** Implement AI-enhanced prediction for human-related game audio while preserving stable real-time overlay behavior.
- **Status:** Offline training template is in place (`footstep-ml/`) and ready for data collection, labeling, training, and ONNX export.
- **Scope boundary:** Keep model training and data preparation fully offline; runtime integration into the real-time path remains gated/optional until validated.

### Approved workflow design (current baseline)
1. **Data source and format**
   - Start from pre-recorded gameplay video/audio.
   - Convert source video/audio to **WAV** (`16kHz`, mono, PCM) for training input.
   - Avoid MP3 for training assets to prevent additional lossy artifacts.

2. **Annotation model (segment-based)**
   - Label short ranges, not single timestamps.
   - Event labels for training input: `Footstep`, `Gunshot`, `Ambience`, `Mixed`, `Other`, `Background`, `NonFootstep`.
   - Runtime class mapping: `Footstep -> 0`, `Gunshot -> 1`, all others -> `Other (2)`.
   - Optional source labeling for self-suppression: `self_label` (`1=self`, `0=not self`, empty=unknown) or `source` (`self/other/enemy/teammate/unknown`).

3. **Model baseline**
   - Use multi-head CNN in `footstep-ml/src/model_cnn.py`:
     - `event_head`: 3-class logits (`Footstep/Gunshot/Other`).
     - `human_head`: binary human-event logit.
     - `self_head`: binary self-event logit.
   - Baseline trigger idea: human event true AND self event false.

4. **Training and export**
   - Prepare sliced clips/manifests via `footstep-ml/src/prepare_data.py`.
   - Train via `footstep-ml/src/train.py`; evaluate via `footstep-ml/src/eval.py`.
   - Export ONNX via `footstep-ml/src/export_onnx.py` plus `.meta.json`.

5. **Validation loop**
   - Validate offline metrics first (`human_f1`, false positives, self suppression behavior).
   - Then perform in-game A/B checks with feature-gated runtime integration.
   - Promote defaults only after stable behavior across multiple sessions/maps.

### Why this design is preferred
- Segment-based labels are robust for transient FPS audio and easier to scale than point labels.
- Multi-head output better matches practical goals (`human event` + `self suppression`) than single-head strict class prediction.
- Keeping data prep/training/export offline minimizes regression risk in the real-time overlay pipeline.

## Annotation Template (30s clip quick start)

Use one row per labeled segment:

```csv
wav_path,start_sec,end_sec,label,session_id,self_label
data/raw/demo_001.wav,0.80,1.20,Footstep,delta_s01,0
data/raw/demo_001.wav,1.55,1.92,Footstep,delta_s01,0
data/raw/demo_001.wav,2.40,2.52,Gunshot,delta_s01,0
data/raw/demo_001.wav,3.10,3.80,Ambience,delta_s01,
data/raw/demo_001.wav,4.20,4.58,Footstep,delta_s01,1
data/raw/demo_001.wav,5.05,5.16,Gunshot,delta_s01,1
data/raw/demo_001.wav,6.20,7.40,Other,delta_s01,
```

Practical labeling rules:
- Do not mark long sparse ranges (for example `3-10s`) as one `Footstep` segment if there are clear gaps.
- For footsteps, prefer short burst ranges (`~0.2s - 0.8s`) and split when silence gaps are obvious.
- For gunshots, use short transients (`~0.08s - 0.3s`).
- If event type is unclear due to overlap, use `Mixed` (mapped to `Other`).
- `self_label` can be empty when uncertain; training will skip self-loss for that sample.

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
   - Purpose: derive profile-specific high-pass/low-pass ranges beyond previous fixed 120-3500 Hz (currently tuned to 90-1200 Hz for low-end + mid-high suppression).
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
