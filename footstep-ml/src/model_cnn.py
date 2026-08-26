from __future__ import annotations

import torch
from torch import nn


class ConvBlock(nn.Module):
    def __init__(self, in_channels: int, out_channels: int, pool: tuple[int, int]) -> None:
        super().__init__()
        self.block = nn.Sequential(
            nn.Conv2d(in_channels, out_channels, kernel_size=3, padding=1),
            nn.BatchNorm2d(out_channels),
            nn.ReLU(inplace=True),
            nn.Conv2d(out_channels, out_channels, kernel_size=3, padding=1),
            nn.BatchNorm2d(out_channels),
            nn.ReLU(inplace=True),
            nn.MaxPool2d(kernel_size=pool),
        )

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.block(x)


class FootstepCNN(nn.Module):
    def __init__(self, in_channels: int = 1, num_classes: int = 3) -> None:
        super().__init__()
        self.features = nn.Sequential(
            ConvBlock(in_channels, 16, pool=(2, 2)),
            ConvBlock(16, 32, pool=(2, 2)),
            ConvBlock(32, 64, pool=(2, 2)),
        )
        self.pool = nn.Sequential(
            nn.AdaptiveAvgPool2d((1, 1)),
            nn.Flatten(),
            nn.Dropout(0.2),
        )
        self.event_head = nn.Linear(64, num_classes)
        self.human_head = nn.Linear(64, 1)
        self.self_head = nn.Linear(64, 1)

    def forward(self, x: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        x = self.features(x)
        x = self.pool(x)
        event_logits = self.event_head(x)
        human_logit = self.human_head(x).squeeze(1)
        self_logit = self.self_head(x).squeeze(1)
        return event_logits, human_logit, self_logit
