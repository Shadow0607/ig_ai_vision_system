import os
import uuid
import numpy as np
import logging
import cv2
from io import BytesIO
from deepface import DeepFace
from qdrant_client import QdrantClient
from qdrant_client.http import models

logger = logging.getLogger(__name__)

class FeatureBankManager:
    def __init__(self, router, detector_backend: str = "retinaface"):
        self.router = router
        self.detector_backend = detector_backend
        
        # 🌟 1. 初始化 Qdrant 連線
        qdrant_host = os.getenv("QDRANT_HOST", "localhost")
        qdrant_port = int(os.getenv("QDRANT_PORT", 6333))
        
        try:
            self.qdrant = QdrantClient(host=qdrant_host, port=qdrant_port)
            self.collection_name = "ig_faces"
            self.vector_size = 4096  # 💡 VGG-Face 模型的預設特徵維度
            
            self._ensure_collection_exists()
        except Exception as e:
            logger.error(f"❌ Qdrant 連線失敗，請確認 Docker 是否已啟動: {e}")

    # core_logic/feature_bank_manager.py 修正版

    def _ensure_collection_exists(self):
        """檢查並建立 Qdrant 的向量資料表 (Collection)"""
        # 🌟 1. 設定目標維度為 4096
        self.vector_size = 4096 
        
        collections = self.qdrant.get_collections().collections
        exists = any(c.name == self.collection_name for c in collections)
        
        # 🌟 2. 如果集合已存在，檢查其維度是否為 4096
        if exists:
            info = self.qdrant.get_collection(self.collection_name)
            current_dim = info.config.params.vectors.size
            
            if current_dim != self.vector_size:
                logger.warning(f"⚠️ 偵測到維度不符 ({current_dim} vs {self.vector_size})，正在刪除舊庫並重新建立...")
                self.qdrant.delete_collection(self.collection_name)
                exists = False # 標記為不存在，讓下方程式碼重新建立

        # 🌟 3. 建立或重建集合
        if not exists:
            logger.info(f"🆕 初始化建立 Qdrant Collection: {self.collection_name} (維度: {self.vector_size})")
            self.qdrant.create_collection(
                collection_name=self.collection_name,
                vectors_config=models.VectorParams(
                    size=self.vector_size, 
                    distance=models.Distance.COSINE
                )
            )
            # 建立索引加速檢索
            self.qdrant.create_payload_index(
                collection_name=self.collection_name,
                field_name="system_name",
                field_schema=models.PayloadSchemaType.KEYWORD,
            )

    def extract_features_and_payloads(self, system_name: str, folder_name: str, label: str) -> list:
        """從 S3 讀取影像，萃取特徵並打包成 Qdrant 支援的 Point 格式"""
        points = []
        prefix = f"{system_name}/{folder_name}/"
        
        try:
            response = self.router.s3_client.list_objects_v2(
                Bucket=self.router.bucket_name, Prefix=prefix
            )
            if 'Contents' not in response: return []

            for obj in response['Contents']:
                s3_key = obj['Key']
                if not s3_key.lower().endswith(('.jpg', '.jpeg', '.png')): continue
                
                # 檔案不落地讀取
                stream = self.router.get_object_stream(s3_key)
                if not stream: continue
                
                img_array = cv2.imdecode(np.frombuffer(stream.read(), np.uint8), cv2.IMREAD_COLOR)
                if img_array is None: continue
                
                # 萃取特徵
                embeds = DeepFace.represent(
                    img_array, 
                    model_name="VGG-Face", 
                    enforce_detection=False, 
                    detector_backend=self.detector_backend
                )
                
                if embeds:
                    # 💡 使用 S3 路徑產生固定的 UUID，避免重複插入相同的圖片
                    point_id = str(uuid.uuid5(uuid.NAMESPACE_URL, s3_key))
                    points.append(
                        models.PointStruct(
                            id=point_id,
                            vector=embeds[0]["embedding"],
                            payload={
                                "system_name": system_name,
                                "label": label,  # "pos" 或 "neg"
                                "s3_key": s3_key
                            }
                        )
                    )
        except Exception as e:
            logger.error(f"❌ 從 S3 提取 {label} 特徵失敗: {e}")
            
        return points

    def build_feature_bank(self, system_name: str):
        """🌟 從 S3 讀取圖片，直接寫入 Qdrant 向量資料庫"""
        logger.info(f"🏗️ [Qdrant Rebuild] 重建向量特徵庫: {system_name}")
        
        # 1. 殺光舊有數據：刪除 Qdrant 中該人物的所有特徵 (因為我們要重新掃描 S3)
        self.qdrant.delete(
            collection_name=self.collection_name,
            points_selector=models.Filter(
                must=[
                    models.FieldCondition(
                        key="system_name",
                        match=models.MatchValue(value=system_name)
                    )
                ]
            )
        )
        
        # 2. 萃取新的特徵向量與標籤
        pos_points = self.extract_features_and_payloads(system_name, "pos", "pos")
        neg_points = self.extract_features_and_payloads(system_name, "GARBAGE", "neg")
        
        all_points = pos_points + neg_points
        
        # 3. 一次性批次寫入 Qdrant
        if all_points:
            self.qdrant.upsert(
                collection_name=self.collection_name,
                points=all_points
            )
            logger.info(f"✅ Qdrant 寫入完成！正樣本: {len(pos_points)} 筆, 負樣本: {len(neg_points)} 筆")
        else:
            logger.warning(f"⚠️ {system_name} 目前沒有正負樣本。")

    def load_feature_banks(self, system_name: str) -> tuple:
        """
        🚀 廢棄聲明：
        因為我們已經導入 Qdrant，S2 Worker 不再需要預先載入龐大的 Numpy 陣列到記憶體。
        這個方法我們暫時回傳 None，以相容舊有的 main_consumer.py，
        下一步我們將會把 AI 判定的邏輯全面轉向 Qdrant Search。
        """
        return None, None, None