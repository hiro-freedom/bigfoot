from __future__ import annotations

import argparse
import json
from pathlib import Path

import torch
from torch import nn
from torch.utils.data import DataLoader

from dataset import AudioFeatureConfig, FootstepDataset
from model_cnn import FootstepCNN


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Train 3-class classifier: footstep/gunshot/other")
    parser.add_argument("--train_csv", required=True)
    parser.add_argument("--val_csv", required=True)
    parser.add_argument("--epochs", type=int, default=40)
    parser.add_argument("--batch_size", type=int, default=64)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--weight_decay", type=float, default=1e-4)
    parser.add_argument("--num_workers", type=int, default=0)
    parser.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu")
    parser.add_argument("--out_dir", default="models/checkpoints")
    parser.add_argument(
        "--class_names",
        default="footstep,gunshot,other",
        help="Comma-separated names for labels 0..N-1",
    )
    parser.add_argument(
        "--human_threshold",
        type=float,
        default=0.5,
        help="Human-event threshold on p(footstep)+p(gunshot)",
    )
    parser.add_argument("--event_loss_weight", type=float, default=1.0)
    parser.add_argument("--human_loss_weight", type=float, default=0.5)
    parser.add_argument("--self_loss_weight", type=float, default=0.25)
    return parser.parse_args()


@torch.no_grad()
def evaluate(
    model: nn.Module,
    loader: DataLoader,
    device: str,
    class_names: list[str],
    human_threshold: float,
    event_loss_weight: float = 1.0,
    human_loss_weight: float = 0.5,
    self_loss_weight: float = 0.25,
) -> dict[str, float]:
    model.eval()
    event_loss_fn = nn.CrossEntropyLoss()
    binary_loss_fn = nn.BCEWithLogitsLoss(reduction="none")
    total_loss = 0.0
    total = 0
    num_classes = len(class_names)
    conf = torch.zeros((num_classes, num_classes), dtype=torch.long)

    human_tp = human_fp = human_fn = human_tn = 0
    self_tp = self_fp = self_fn = self_tn = 0
    self_total = 0

    for x, y_event, y_self, y_self_mask in loader:
        x = x.to(device)
        y_event = y_event.to(device)
        y_self = y_self.to(device)
        y_self_mask = y_self_mask.to(device)

        event_logits, human_logit, self_logit = model(x)

        event_loss = event_loss_fn(event_logits, y_event)

        y_human = (y_event != 2).float()
        human_loss = binary_loss_fn(human_logit, y_human).mean()

        self_loss_raw = binary_loss_fn(self_logit, y_self)
        self_loss = (self_loss_raw * y_self_mask).sum() / y_self_mask.sum().clamp_min(1.0)

        loss = (
            event_loss_weight * event_loss
            + human_loss_weight * human_loss
            + self_loss_weight * self_loss
        )

        total_loss += loss.item() * x.size(0)
        total += x.size(0)

        probs = torch.softmax(event_logits, dim=1)
        pred = torch.argmax(probs, dim=1)

        y_list = y_event.detach().cpu().tolist()
        pred_list = pred.detach().cpu().tolist()
        for true_idx, pred_idx in zip(y_list, pred_list):
            conf[true_idx, pred_idx] += 1

        human_score = probs[:, 0] + probs[:, 1]
        pred_human = human_score >= human_threshold
        true_human = y_event != 2
        human_tp += int((pred_human & true_human).sum().item())
        human_fp += int((pred_human & (~true_human)).sum().item())
        human_fn += int(((~pred_human) & true_human).sum().item())
        human_tn += int(((~pred_human) & (~true_human)).sum().item())

        pred_self = torch.sigmoid(self_logit) >= 0.5
        true_self = y_self >= 0.5
        valid_mask = y_self_mask >= 0.5
        self_total += int(valid_mask.sum().item())
        if valid_mask.any():
            self_tp += int((pred_self & true_self & valid_mask).sum().item())
            self_fp += int((pred_self & (~true_self) & valid_mask).sum().item())
            self_fn += int(((~pred_self) & true_self & valid_mask).sum().item())
            self_tn += int(((~pred_self) & (~true_self) & valid_mask).sum().item())

    avg_loss = total_loss / max(total, 1)
    accuracy = float(conf.diag().sum().item()) / max(int(conf.sum().item()), 1)

    class_recall_sum = 0.0
    for class_idx in range(num_classes):
        tp_i = float(conf[class_idx, class_idx].item())
        total_i = float(conf[class_idx, :].sum().item())
        class_recall_sum += tp_i / (total_i + 1e-9)
    macro_recall = class_recall_sum / num_classes

    human_precision = human_tp / (human_tp + human_fp + 1e-9)
    human_recall = human_tp / (human_tp + human_fn + 1e-9)
    human_f1 = 2 * human_precision * human_recall / (human_precision + human_recall + 1e-9)

    self_precision = self_tp / (self_tp + self_fp + 1e-9)
    self_recall = self_tp / (self_tp + self_fn + 1e-9)
    self_f1 = 2 * self_precision * self_recall / (self_precision + self_recall + 1e-9)

    return {
        "loss": avg_loss,
        "accuracy": accuracy,
        "macro_recall": macro_recall,
        "human_precision": human_precision,
        "human_recall": human_recall,
        "human_f1": human_f1,
        "self_precision": self_precision,
        "self_recall": self_recall,
        "self_f1": self_f1,
        "self_count": float(self_total),
    }


def main() -> None:
    args = parse_args()
    Path(args.out_dir).mkdir(parents=True, exist_ok=True)

    class_names = [name.strip() for name in args.class_names.split(",") if name.strip()]
    if len(class_names) != 3:
        raise ValueError("Expected exactly 3 classes, e.g. footstep,gunshot,other")

    feature_cfg = AudioFeatureConfig()
    train_ds = FootstepDataset(args.train_csv, feature_cfg)
    val_ds = FootstepDataset(args.val_csv, feature_cfg)

    train_loader = DataLoader(
        train_ds,
        batch_size=args.batch_size,
        shuffle=True,
        num_workers=args.num_workers,
        pin_memory=True,
    )
    val_loader = DataLoader(
        val_ds,
        batch_size=args.batch_size,
        shuffle=False,
        num_workers=args.num_workers,
        pin_memory=True,
    )

    model = FootstepCNN(num_classes=len(class_names)).to(args.device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)
    event_loss_fn = nn.CrossEntropyLoss()
    binary_loss_fn = nn.BCEWithLogitsLoss(reduction="none")

    best_human_f1 = -1.0
    best_path = Path(args.out_dir) / "best.pt"

    for epoch in range(1, args.epochs + 1):
        model.train()
        running_loss = 0.0
        total = 0

        for x, y_event, y_self, y_self_mask in train_loader:
            x = x.to(args.device)
            y_event = y_event.to(args.device)
            y_self = y_self.to(args.device)
            y_self_mask = y_self_mask.to(args.device)

            optimizer.zero_grad(set_to_none=True)
            event_logits, human_logit, self_logit = model(x)

            event_loss = event_loss_fn(event_logits, y_event)

            y_human = (y_event != 2).float()
            human_loss = binary_loss_fn(human_logit, y_human).mean()

            self_loss_raw = binary_loss_fn(self_logit, y_self)
            self_loss = (self_loss_raw * y_self_mask).sum() / y_self_mask.sum().clamp_min(1.0)

            loss = (
                args.event_loss_weight * event_loss
                + args.human_loss_weight * human_loss
                + args.self_loss_weight * self_loss
            )
            loss.backward()
            optimizer.step()

            running_loss += loss.item() * x.size(0)
            total += x.size(0)

        train_loss = running_loss / max(total, 1)
        metrics = evaluate(
            model,
            val_loader,
            args.device,
            class_names=class_names,
            human_threshold=args.human_threshold,
            event_loss_weight=args.event_loss_weight,
            human_loss_weight=args.human_loss_weight,
            self_loss_weight=args.self_loss_weight,
        )

        print(
            f"epoch={epoch:03d} "
            f"train_loss={train_loss:.4f} "
            f"val_loss={metrics['loss']:.4f} "
            f"val_acc={metrics['accuracy']:.4f} "
            f"val_macro_recall={metrics['macro_recall']:.4f} "
            f"val_human_f1={metrics['human_f1']:.4f} "
            f"val_self_f1={metrics['self_f1']:.4f}"
        )

        if metrics["human_f1"] > best_human_f1:
            best_human_f1 = metrics["human_f1"]
            torch.save(
                {
                    "model_state": model.state_dict(),
                    "feature_config": feature_cfg.__dict__,
                    "class_names": class_names,
                    "human_threshold": args.human_threshold,
                    "self_threshold": 0.5,
                    "loss_weights": {
                        "event": args.event_loss_weight,
                        "human": args.human_loss_weight,
                        "self": args.self_loss_weight,
                    },
                },
                best_path,
            )
            print(f"saved best checkpoint -> {best_path}")

    metrics_path = Path(args.out_dir) / "training_config.json"
    metrics_path.write_text(
        json.dumps(
            {
                "class_names": class_names,
                "human_threshold": args.human_threshold,
                "self_threshold": 0.5,
                "loss_weights": {
                    "event": args.event_loss_weight,
                    "human": args.human_loss_weight,
                    "self": args.self_loss_weight,
                },
                "feature_config": feature_cfg.__dict__,
            },
            indent=2,
        ),
        encoding="utf-8",
    )

    print(f"training complete, best val_human_f1={best_human_f1:.4f}")
    print(f"saved config -> {metrics_path}")


if __name__ == "__main__":
    main()
