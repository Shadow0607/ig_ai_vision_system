import torch
import torch.nn as nn

class FaceClassifier(nn.Module):
    """
    專屬特徵分類神經網路
    用於接收 DeepFace 提取的臉部特徵向量，並輸出是否為本人的機率值 (0.0 ~ 1.0)
    """
    def __init__(self, input_dim=4096): 
        # VGG-Face 預設特徵維度為 4096
        super(FaceClassifier, self).__init__()
        self.network = nn.Sequential(
            nn.Linear(input_dim, 256),
            nn.ReLU(),
            nn.Dropout(0.3),
            nn.Linear(256, 64),
            nn.ReLU(),
            nn.Linear(64, 1),
            nn.Sigmoid() # 輸出 0.0 ~ 1.0 的機率值
        )

    def forward(self, x):
        """前向傳播邏輯"""
        return self.network(x)