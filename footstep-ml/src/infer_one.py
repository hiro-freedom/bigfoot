from __future__ import annotations

import argparse
import json
from pathlib import Path

import onnxruntime as ort
import torch
import torchaudio


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run one-file ONNX inference")
    parser.add_argument("--onnx", required=True)
    parser.add_argument("--wav", required=True)
    parser.add_argument("--sample_rate", type=int, default=16000)
    parser.add_argument("--n_fft", type=int, default=512)
    parser.add_argument("--hop_length", type=int, default=160)
    parser.add_argument("--n_mels", type=int, default=64)
    parser.add_argument("--human_threshold", type=float, default=0.5)
    parser.add_argument("--self_threshold", type=float, default=0.5)
    return parser.parse_args()


def main() -> None:
    args = parse_args()

    waveform, sr = torchaudio.load(args.wav)
    if waveform.size(0) > 1:
        waveform = waveform.mean(dim=0, keepdim=True)
    if sr != args.sample_rate:
        waveform = torchaudio.functional.resample(waveform, sr, args.sample_rate)

    mel_transform = torchaudio.transforms.MelSpectrogram(
        sample_rate=args.sample_rate,
        n_fft=args.n_fft,
        hop_length=args.hop_length,
        n_mels=args.n_mels,
        center=True,
        power=2.0,
    )
    mel = mel_transform(waveform)
    log_mel = torch.log(mel + 1e-6).unsqueeze(0).numpy()

    session = ort.InferenceSession(args.onnx, providers=["CPUExecutionProvider"])
    outputs = session.run(None, {"input": log_mel})
    event_logits = torch.tensor(outputs[0][0], dtype=torch.float32)
    human_logit = float(outputs[1][0])
    self_logit = float(outputs[2][0])

    probs = torch.softmax(event_logits, dim=0)
    prob_human_head = 1.0 / (1.0 + torch.exp(torch.tensor(-human_logit)).item())
    prob_self = 1.0 / (1.0 + torch.exp(torch.tensor(-self_logit)).item())

    class_names = ["footstep", "gunshot", "other"]
    meta_path = Path(args.onnx).with_suffix(".meta.json")
    human_threshold = args.human_threshold
    self_threshold = args.self_threshold
    if meta_path.exists():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        class_names = meta.get("class_names", class_names)
        human_threshold = float(meta.get("human_threshold", human_threshold))
        self_threshold = float(meta.get("self_threshold", self_threshold))

    pred_idx = int(torch.argmax(probs).item())
    human_score = float(probs[0].item() + probs[1].item())
    is_human = human_score >= human_threshold
    is_self = prob_self >= self_threshold
    should_trigger = is_human and (not is_self)

    print(f"predicted_class={class_names[pred_idx]}")
    print(f"prob_footstep={probs[0].item():.5f}")
    print(f"prob_gunshot={probs[1].item():.5f}")
    print(f"prob_other={probs[2].item():.5f}")
    print(f"human_score={human_score:.5f}")
    print(f"human_head_probability={prob_human_head:.5f}")
    print(f"self_probability={prob_self:.5f}")
    print(f"human_threshold={human_threshold:.5f}")
    print(f"self_threshold={self_threshold:.5f}")
    print(f"is_human_event={is_human}")
    print(f"is_self_event={is_self}")
    print(f"should_trigger={should_trigger}")


if __name__ == "__main__":
    main()
