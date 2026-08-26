from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import csv

import torch
import torchaudio
from torch.utils.data import Dataset


@dataclass
class AudioFeatureConfig:
    sample_rate: int = 16000
    n_fft: int = 512
    hop_length: int = 160
    n_mels: int = 64


class FootstepDataset(Dataset):
    def __init__(self, csv_path: str, feature_config: AudioFeatureConfig | None = None) -> None:
        self.csv_path = Path(csv_path)
        if not self.csv_path.exists():
            raise FileNotFoundError(f"CSV file not found: {self.csv_path}")

        self.feature_config = feature_config or AudioFeatureConfig()
        self.items = self._load_items(self.csv_path)

        self.mel_transform = torchaudio.transforms.MelSpectrogram(
            sample_rate=self.feature_config.sample_rate,
            n_fft=self.feature_config.n_fft,
            hop_length=self.feature_config.hop_length,
            n_mels=self.feature_config.n_mels,
            center=True,
            power=2.0,
        )

    @staticmethod
    def _load_items(csv_path: Path) -> list[tuple[str, int, int, int]]:
        with csv_path.open("r", encoding="utf-8", newline="") as f:
            reader = csv.DictReader(f)
            fieldnames = set(reader.fieldnames or [])
            if "clip_path" not in fieldnames or "label" not in fieldnames:
                raise ValueError("CSV must contain clip_path and label columns")
            has_self_label = "self_label" in fieldnames

            items: list[tuple[str, int, int, int]] = []
            for row in reader:
                clip_path = (row.get("clip_path") or "").strip()
                if not clip_path:
                    continue
                label = int(row["label"])
                if has_self_label and (row.get("self_label") or "").strip() != "":
                    self_label = int(row["self_label"])
                    self_mask = 1
                else:
                    self_label = 0
                    self_mask = 0
                items.append((clip_path, label, self_label, self_mask))
            return items


    def __len__(self) -> int:
        return len(self.items)

    def __getitem__(self, index: int) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]:
        clip_path, label, self_label, self_mask = self.items[index]
        waveform, sample_rate = torchaudio.load(clip_path)

        if waveform.size(0) > 1:
            waveform = waveform.mean(dim=0, keepdim=True)

        if sample_rate != self.feature_config.sample_rate:
            waveform = torchaudio.functional.resample(
                waveform,
                orig_freq=sample_rate,
                new_freq=self.feature_config.sample_rate,
            )

        mel = self.mel_transform(waveform)
        log_mel = torch.log(mel + 1e-6)
        return (
            log_mel,
            torch.tensor(label, dtype=torch.long),
            torch.tensor(self_label, dtype=torch.float32),
            torch.tensor(self_mask, dtype=torch.float32),
        )
