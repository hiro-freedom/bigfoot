from __future__ import annotations

import argparse
import json
from pathlib import Path

import torch

from model_cnn import FootstepCNN


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Export trained 3-class model checkpoint to ONNX")
    parser.add_argument("--ckpt", required=True)
    parser.add_argument("--out", required=True, help="Output .onnx path")
    parser.add_argument("--opset", type=int, default=17)
    parser.add_argument("--human_threshold", type=float, default=None)
    parser.add_argument("--sample_frames", type=int, default=33, help="Time frames for dummy input")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    checkpoint = torch.load(args.ckpt, map_location="cpu")
    feature_config = checkpoint.get("feature_config", {})
    class_names = checkpoint.get("class_names", ["footstep", "gunshot", "other"])
    human_threshold = (
        float(checkpoint.get("human_threshold", 0.5))
        if args.human_threshold is None
        else args.human_threshold
    )

    model = FootstepCNN(num_classes=len(class_names))
    model.load_state_dict(checkpoint["model_state"])
    model.eval()

    n_mels = int(feature_config.get("n_mels", 64))
    dummy = torch.randn(1, 1, n_mels, args.sample_frames, dtype=torch.float32)

    torch.onnx.export(
        model,
        dummy,
        str(out_path),
        input_names=["input"],
        output_names=["event_logits", "human_logit", "self_logit"],
        dynamic_axes={
            "input": {0: "batch", 3: "frames"},
            "event_logits": {0: "batch"},
            "human_logit": {0: "batch"},
            "self_logit": {0: "batch"},
        },
        opset_version=args.opset,
    )

    meta_path = out_path.with_suffix(".meta.json")
    meta = {
        "model_type": "FootstepCNN",
        "task": "multiclass_footstep_gunshot_other",
        "class_names": class_names,
        "human_event_classes": [class_names[0], class_names[1]],
        "human_threshold": human_threshold,
        "self_threshold": float(checkpoint.get("self_threshold", 0.5)),
        "feature_config": {
            "sample_rate": int(feature_config.get("sample_rate", 16000)),
            "n_fft": int(feature_config.get("n_fft", 512)),
            "hop_length": int(feature_config.get("hop_length", 160)),
            "n_mels": int(feature_config.get("n_mels", 64)),
        },
    }
    meta_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")

    print(f"onnx exported: {out_path}")
    print(f"meta exported: {meta_path}")


if __name__ == "__main__":
    main()
