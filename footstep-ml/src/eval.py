from __future__ import annotations

import argparse

import torch
from torch.utils.data import DataLoader

from dataset import AudioFeatureConfig, FootstepDataset
from model_cnn import FootstepCNN
from train import evaluate


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Evaluate 3-class footstep/gunshot/other checkpoint")
    parser.add_argument("--test_csv", required=True)
    parser.add_argument("--ckpt", required=True)
    parser.add_argument("--batch_size", type=int, default=64)
    parser.add_argument("--num_workers", type=int, default=0)
    parser.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu")
    parser.add_argument("--human_threshold", type=float, default=None)
    return parser.parse_args()


def main() -> None:
    args = parse_args()

    checkpoint = torch.load(args.ckpt, map_location=args.device)
    feature_cfg = AudioFeatureConfig(**checkpoint.get("feature_config", {}))
    class_names = checkpoint.get("class_names", ["footstep", "gunshot", "other"])
    loss_weights = checkpoint.get("loss_weights", {"event": 1.0, "human": 0.5, "self": 0.25})
    human_threshold = (
        float(checkpoint.get("human_threshold", 0.5))
        if args.human_threshold is None
        else args.human_threshold
    )

    dataset = FootstepDataset(args.test_csv, feature_cfg)
    loader = DataLoader(
        dataset,
        batch_size=args.batch_size,
        shuffle=False,
        num_workers=args.num_workers,
        pin_memory=True,
    )

    model = FootstepCNN(num_classes=len(class_names)).to(args.device)
    model.load_state_dict(checkpoint["model_state"])

    metrics = evaluate(
        model,
        loader,
        args.device,
        class_names=class_names,
        human_threshold=human_threshold,
        event_loss_weight=float(loss_weights.get("event", 1.0)),
        human_loss_weight=float(loss_weights.get("human", 0.5)),
        self_loss_weight=float(loss_weights.get("self", 0.25)),
    )
    print(f"test_loss={metrics['loss']:.4f}")
    print(f"test_accuracy={metrics['accuracy']:.4f}")
    print(f"test_macro_recall={metrics['macro_recall']:.4f}")
    print(f"test_human_precision={metrics['human_precision']:.4f}")
    print(f"test_human_recall={metrics['human_recall']:.4f}")
    print(f"test_human_f1={metrics['human_f1']:.4f}")
    if metrics["self_count"] > 0:
        print(f"test_self_precision={metrics['self_precision']:.4f}")
        print(f"test_self_recall={metrics['self_recall']:.4f}")
        print(f"test_self_f1={metrics['self_f1']:.4f}")
    else:
        print("test_self_metrics=not_available (self_label missing)")


if __name__ == "__main__":
    main()
