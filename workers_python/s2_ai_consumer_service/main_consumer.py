import os
import sys
import json
import redis
import logging
import cv2
import numpy as np
from pathlib import Path
from dotenv import load_dotenv
from io import BytesIO
from qdrant_client.http import models

# 匯入核心模組
from models.feature_extractor import FeatureExtractor
from core_logic.feature_bank_manager import FeatureBankManager
from core_logic.decision_engine import DecisionEngine
from storage.file_router import FileAndDBRouter

# 環境變數與日誌設定
script_dir = Path(__file__).resolve().parent
env_path = script_dir.parent.parent / '.env'
load_dotenv(dotenv_path=env_path)

logging.basicConfig(level=logging.INFO, format='%(asctime)s - [S2-S3] - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

QUEUE_NAME = "ig_processing_queue"

class AIConsumer:
    def __init__(self):
        self.router = FileAndDBRouter(
            db_host=os.getenv('DB_HOST'),
            db_port=os.getenv('DB_PORT'),
            db_user=os.getenv('DB_USER'),
            db_password=os.getenv('DB_PASSWORD'),
            db_name=os.getenv('DB_NAME')
        )
        
        self.redis = self._connect_redis()
        
        self.extractor = FeatureExtractor(
            detector_backend="retinaface" if "linux" in sys.platform else "mtcnn"
        )
        
        self.bank_manager = FeatureBankManager(
            router=self.router, 
            detector_backend=self.extractor.detector_backend
        )

        # 🌟 已移除：不再需要 self.global_neg_bank 與 _load_global_neg_bank()，全面交給 Qdrant

    def _connect_redis(self):
        pool = redis.ConnectionPool(
            host=os.getenv('REDIS_HOST'),
            port=int(os.getenv('REDIS_PORT')),
            password=os.getenv('REDIS_PASSWORD'),
            decode_responses=True
        )
        return redis.Redis(connection_pool=pool)

    def _is_cold_start(self, profile: str) -> bool:
        """🌟 檢查 Qdrant 內是否已有該人物的正樣本"""
        try:
            result = self.bank_manager.qdrant.count(
                collection_name="ig_faces",
                count_filter=models.Filter(
                    must=[
                        models.FieldCondition(key="system_name", match=models.MatchValue(value=profile)),
                        models.FieldCondition(key="label", match=models.MatchValue(value="pos"))
                    ]
                )
            )
            return result.count == 0
        except Exception as e:
            logger.error(f"⚠️ Qdrant 查詢失敗，預設視為冷啟動: {e}")
            return True

    def _route_and_log(self, media_id: int, profile: str, current_ai_key: str, target_folder: str, status: str, face_detected: bool, score: float = 0.0):
        if not media_id: return

        raw_db_path = self.router.get_real_db_path(media_id)
        if not raw_db_path: return

        # 🌟 關鍵修正：將 DB 中的 s3:// 絕對路徑轉為 boto3 認識的相對 Object Key
        bucket_prefix = f"s3://{self.router.bucket_name}/"
        main_s3_key = raw_db_path.replace(bucket_prefix, "") if raw_db_path.startswith(bucket_prefix) else raw_db_path

        # 🌟 呼叫 FileRouter 進行純雲端搬移 (Copy + Delete)
        new_main_key = self.router.move_file_safe(main_s3_key, target_folder)
        
        if main_s3_key and main_s3_key.lower().endswith('.mp4'):
            ai_thumb_key = main_s3_key.rsplit('.', 1)[0] + ".jpg"
            self.router.move_file_safe(ai_thumb_key, target_folder)

        if new_main_key:
            self.router.update_media_asset_path(media_id, new_main_key)
            self.router.update_db_log(media_id, status, face_detected, score)
            
            try:
                self.redis.publish("ai_task_completed", "done")
            except Exception as e:
                pass

    def process_task(self, task_json: str):
        task = json.loads(task_json)
        profile = task.get("profile")
        
        if task.get("type") == "BUILD_FEATURE_BANK":
            self.bank_manager.build_feature_bank(profile)
            return

        s3_uri = task.get('file_path')
        media_id = task.get('task_id')
        if not s3_uri or not media_id:
            logger.warning(f"⚠️ 媒體任務資料不完整: {task}")
            return

        # 🌟 關鍵修正：把 S1 送來的 s3:// 前綴濾掉
        bucket_prefix = f"s3://{self.router.bucket_name}/"
        s3_key = s3_uri.replace(bucket_prefix, "") if s3_uri.startswith(bucket_prefix) else s3_uri

        # 1. 檔案不落地獲取串流
        if not (stream := self.router.get_object_stream(s3_key)):
            logger.warning(f"❌ S3 物件不存在: {s3_key}")
            return

        # 2. 影像解碼與特徵提取
        img_array = cv2.imdecode(np.frombuffer(stream.read(), np.uint8), cv2.IMREAD_COLOR)
        if img_array is None:
            self._route_and_log(media_id, profile, s3_key, "ERROR", "ERROR_READ", False)
            return

        target_embedding, face_detected = self.extractor.extract_target_embedding(img_array)
        if not face_detected:
            self._route_and_log(media_id, profile, s3_key, "NOFACE", "NOFACE", False)
            return

        # 3. 冷啟動檢查
        if self._is_cold_start(profile):
            self._route_and_log(media_id, profile, s3_key, "INITIAL_REVIEW", "INITIAL_REVIEW", True)
            return

        # 4. 🧠 呼叫純 Qdrant 決策引擎
        db_threshold = self.router.get_person_threshold(profile)
        result, score, target_folder = DecisionEngine.compare_face_logic(
            target_embedding=target_embedding, 
            system_name=profile,
            qdrant_client=self.bank_manager.qdrant,
            db_threshold=db_threshold
        )

        status_map = {"MATCH_VSTACK": "OUTPUT", "HITL": "HITL", "GARBAGE": "REJECTED"}
        self._route_and_log(media_id, profile, s3_key, target_folder, status_map.get(result, "SKIP"), True, score)

    def run(self):
        logger.info(f"🚀 S2 AI Consumer 啟動 (純 S3 + Qdrant 雲端原生版)")
        queues = ["ig_processing_queue_high", "ig_processing_queue"]
        
        while True:
            if not (result := self.redis.brpop(queues, timeout=10)):
                continue
            
            match result:
                case ("ig_processing_queue_high", task_json):
                    logger.info("⚡ [HIGH PRIORITY] 處理高優先級任務")
                    self._safe_execute(task_json)
                case (_, task_json):
                    self._safe_execute(task_json)

    def _safe_execute(self, task_json):
        try:
            self.process_task(task_json)
        except Exception as e:
            logger.error(f"❌ 任務崩潰: {e}")

if __name__ == "__main__":
    consumer = AIConsumer()
    consumer.run()