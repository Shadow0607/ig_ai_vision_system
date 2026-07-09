# IG AI Vision System - S2 AI Consumer Service

本子系統為 Python 實作的 AI 視覺辨識服務，扮演資料消費者 (Consumer) 的角色，負責從佇列中取出圖片/影片進行人臉特徵提取與比對。

## 技術棧
- **語言**：Python 3
- **AI 處理**：OpenCV, Numpy
- **人臉辨識後端**：RetinaFace (Linux) / MTCNN
- **向量資料庫庫件**：Qdrant Client
- **併發控制**：ThreadPoolExecutor (多執行緒)

## 核心邏輯 (`main_consumer.py`)
1. **監聽任務**：持續監聽 Redis 佇列 `ig_processing_queue` 與 `ig_processing_queue_high`，一旦有新任務即交由執行緒池處理。
2. **取得與解碼檔案**：根據任務指示，從 MinIO 下載目標影像，並透過 OpenCV (`cv2.imdecode`) 載入記憶體。
3. **特徵提取 (`FeatureExtractor`)**：找出影像中的人臉，並轉換為特徵向量 (Embeddings)。若無人臉則直接標記為 `NOFACE`。
4. **Qdrant 向量比對 (`DecisionEngine`)**：
   - **冷啟動 (Cold Start)**：如果 Qdrant 中沒有該人物的基準特徵，會將此圖標記為 `INITIAL_REVIEW` 待人工確認。
   - 依據特徵向量計算相似度分數。分數過門檻判斷為 `OUTPUT` (本人)，稍低為 `PENDING` (待定)，極低則為 `REJECTED` (垃圾桶)。
5. **長影片支援 (`VideoProcessor`)**：若是 .mp4 檔案，會以特定採樣率擷取影格進行綜合判定，避免單一影格失準。
6. **狀態更新**：透過 `FileAndDBRouter` 移動 MinIO 中的實體檔案位置，並更新 MySQL 資料庫的最終辨識狀態。

## 啟動方式
```bash
# 預設會啟動 2 個 Worker 執行緒，可根據硬體效能調整
python main_consumer.py
```
