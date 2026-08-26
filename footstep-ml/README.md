# footstep-ml

Minimal training template for a multi-head detector with:
- event class head: `Footstep`, `Gunshot`, `Other`
- human-event head: `human vs non-human`
- self-event head: `self vs non-self` (optional labels)

## 1) Setup

```bash
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
```

## 2) Prepare input annotations

Create `data/manifests/segments.csv` with this schema:

```csv
wav_path,start_sec,end_sec,label,session_id
data/raw/game1_001.wav,12.30,12.75,Footstep,game1_day1
data/raw/game1_001.wav,22.30,22.55,Gunshot,game1_day1
data/raw/game1_001.wav,45.00,46.20,Ambience,game1_day1
data/raw/game2_003.wav,88.10,88.60,Mixed,game2_day3
```

Rules:
- `label`: `Footstep`, `Gunshot`, `Ambience`, `Mixed`, `Other`, `Background`, or `NonFootstep`
- `session_id`: used for leak-safe split (train/val/test by session)
- `wav_path`: path to existing `.wav` file
- optional `self_label`: `1=self`, `0=not self`, empty if unknown
- optional `source`: `self/other/enemy/teammate/unknown` (used only when `self_label` is not provided)

Label mapping used by `prepare_data.py`:
- `Footstep -> 0`
- `Gunshot -> 1`
- `Ambience/Mixed/Other/Background/NonFootstep -> 2`

Generated manifests contain:
- `clip_path,label,session_id,self_label`
- if `self_label` is empty, self-head loss is skipped for that sample

## 3) Build sliced dataset and manifests

```bash
python src/prepare_data.py --segments data/manifests/segments.csv --out data/processed
```

Generated files:
- `data/processed/train.csv`
- `data/processed/val.csv`
- `data/processed/test.csv`
- `data/processed/clips/*.wav`

## 4) Train

```bash
python src/train.py --train_csv data/processed/train.csv --val_csv data/processed/val.csv --epochs 40
```

Best checkpoint output:
- `models/checkpoints/best.pt`

Optional: set human-event threshold (used for `footstep or gunshot` decision)

```bash
python src/train.py --train_csv data/processed/train.csv --val_csv data/processed/val.csv --epochs 40 --human_threshold 0.6
```

Optional: tune multi-head loss weights

```bash
python src/train.py --train_csv data/processed/train.csv --val_csv data/processed/val.csv --epochs 40 --event_loss_weight 1.0 --human_loss_weight 0.6 --self_loss_weight 0.3
```

## 5) Evaluate

```bash
python src/eval.py --test_csv data/processed/test.csv --ckpt models/checkpoints/best.pt
```

## 6) Export ONNX

```bash
python src/export_onnx.py --ckpt models/checkpoints/best.pt --out models/footstep_cnn_v1.onnx
```

Generated files:
- `models/footstep_cnn_v1.onnx`
- `models/footstep_cnn_v1.meta.json`

## 7) Quick ONNX inference check

```bash
python src/infer_one.py --onnx models/footstep_cnn_v1.onnx --wav data/processed/clips/train_000000_000.wav
```

## Default training/data parameters

- sample rate: `16000`
- window size: `320 ms`
- hop size: `80 ms`
- mel bins: `64`
- fft size: `512`
- mel hop length: `160`

Output includes:
- 3-class probabilities (`footstep`, `gunshot`, `other`)
- `human_score = p(footstep) + p(gunshot)`
- `human_head_probability` and `self_probability`
- `should_trigger = is_human_event and not is_self_event`

## Suggested first target

- keep `human_event` recall high (miss fewer player-related sounds)
- keep false positives manageable in ambience-heavy scenes
