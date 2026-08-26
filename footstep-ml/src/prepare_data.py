from __future__ import annotations

import argparse
import csv
import random
from collections import defaultdict
from typing import Optional
from pathlib import Path

import torch
import torchaudio


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Prepare sliced clips and train/val/test manifests")
    parser.add_argument("--segments", required=True, help="Path to segments.csv")
    parser.add_argument("--out", required=True, help="Output directory for processed data")
    parser.add_argument("--sample-rate", type=int, default=16000)
    parser.add_argument("--window-ms", type=float, default=320.0)
    parser.add_argument("--hop-ms", type=float, default=80.0)
    parser.add_argument("--train-ratio", type=float, default=0.7)
    parser.add_argument("--val-ratio", type=float, default=0.2)
    parser.add_argument("--seed", type=int, default=42)
    return parser.parse_args()


def normalize_label(raw_label: str) -> int:
    label = raw_label.strip().lower()
    if label == "footstep":
        return 0
    if label == "gunshot":
        return 1
    if label in {"ambience", "mixed", "nonfootstep", "other", "background"}:
        return 2
    raise ValueError(f"Unsupported label: {raw_label}")


def read_segments(csv_path: Path) -> list[dict[str, str]]:
    with csv_path.open("r", encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f)
        required = {"wav_path", "start_sec", "end_sec", "label", "session_id"}
        missing = required.difference(set(reader.fieldnames or []))
        if missing:
            raise ValueError(f"segments.csv missing columns: {sorted(missing)}")
        return list(reader)


def split_sessions(
    sessions: list[str], train_ratio: float, val_ratio: float, seed: int
) -> tuple[set[str], set[str], set[str]]:
    random.seed(seed)
    sessions_copy = sessions[:]
    random.shuffle(sessions_copy)

    total = len(sessions_copy)
    train_end = int(total * train_ratio)
    val_end = train_end + int(total * val_ratio)

    train = set(sessions_copy[:train_end])
    val = set(sessions_copy[train_end:val_end])
    test = set(sessions_copy[val_end:])

    if not train or not val or not test:
        raise ValueError("Session split produced empty train/val/test. Add more sessions or adjust ratios.")

    return train, val, test


def parse_self_label(row: dict[str, str]) -> Optional[int]:
    if "self_label" in row and (row.get("self_label") or "").strip() != "":
        value = int((row.get("self_label") or "").strip())
        if value not in (0, 1):
            raise ValueError(f"self_label must be 0 or 1, got: {value}")
        return value

    source_value = (row.get("source") or "").strip().lower()
    if source_value in {"self", "own", "myself"}:
        return 1
    if source_value in {"other", "enemy", "teammate", "unknown"}:
        return 0
    return None


def write_manifest(path: Path, rows: list[tuple[str, int, str, Optional[int]]]) -> None:
    with path.open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(["clip_path", "label", "session_id", "self_label"])
        for clip_path, label, session_id, self_label in rows:
            writer.writerow([clip_path, label, session_id, "" if self_label is None else self_label])


def main() -> None:
    args = parse_args()

    segments_path = Path(args.segments)
    out_root = Path(args.out)
    clips_root = out_root / "clips"
    manifests_root = out_root
    clips_root.mkdir(parents=True, exist_ok=True)
    manifests_root.mkdir(parents=True, exist_ok=True)

    window_samples = int(args.sample_rate * (args.window_ms / 1000.0))
    hop_samples = int(args.sample_rate * (args.hop_ms / 1000.0))

    segments = read_segments(segments_path)
    sessions = sorted({row["session_id"] for row in segments})
    train_sessions, val_sessions, test_sessions = split_sessions(
        sessions=sessions,
        train_ratio=args.train_ratio,
        val_ratio=args.val_ratio,
        seed=args.seed,
    )

    split_rows: dict[str, list[tuple[str, int, str, Optional[int]]]] = defaultdict(list)

    for idx, row in enumerate(segments):
        wav_path = Path(row["wav_path"])
        if not wav_path.exists():
            raise FileNotFoundError(f"WAV not found: {wav_path}")

        start_sec = float(row["start_sec"])
        end_sec = float(row["end_sec"])
        session_id = row["session_id"]
        label = normalize_label(row["label"])
        self_label = parse_self_label(row)

        waveform, sr = torchaudio.load(str(wav_path))
        if waveform.size(0) > 1:
            waveform = waveform.mean(dim=0, keepdim=True)
        if sr != args.sample_rate:
            waveform = torchaudio.functional.resample(waveform, orig_freq=sr, new_freq=args.sample_rate)

        start_sample = max(0, int(start_sec * args.sample_rate))
        end_sample = min(waveform.size(1), int(end_sec * args.sample_rate))
        if end_sample <= start_sample:
            continue

        segment = waveform[:, start_sample:end_sample]
        if segment.size(1) < window_samples:
            continue

        if session_id in train_sessions:
            split = "train"
        elif session_id in val_sessions:
            split = "val"
        else:
            split = "test"

        write_count = 0
        for offset in range(0, segment.size(1) - window_samples + 1, hop_samples):
            clip = segment[:, offset : offset + window_samples]
            clip_name = f"{split}_{idx:06d}_{write_count:03d}.wav"
            clip_path = clips_root / clip_name
            torchaudio.save(str(clip_path), clip, sample_rate=args.sample_rate)
            split_rows[split].append((str(clip_path).replace("\\", "/"), label, session_id, self_label))
            write_count += 1

    write_manifest(manifests_root / "train.csv", split_rows["train"])
    write_manifest(manifests_root / "val.csv", split_rows["val"])
    write_manifest(manifests_root / "test.csv", split_rows["test"])

    print("Data preparation complete")
    print(f"train clips: {len(split_rows['train'])}")
    print(f"val clips:   {len(split_rows['val'])}")
    print(f"test clips:  {len(split_rows['test'])}")


if __name__ == "__main__":
    main()
