import os
import sys
import json
import redis
import logging
import cv2
import numpy as np
from pathlib import Path
from io import BytesIO
from qdrant_client.http import models
from concurrent.futures import ThreadPoolExecutor

# 🌟 1. Bootstrap: 確保 Python 找得到 workers_python 目錄以載入共享模組
workers_dir = str(Path(__file__).resolve().parent.parent)
if workers_dir not in sys.path:
    sys.path.insert(0, workers_dir)

# 🌟 2. 執行統一初始化 (取代舊有的路徑計算與 load_dotenv)
from shared.config_loader import setup_project_env
ROOT_DIR = setup_project_env()

# 匯入核心模組
from models.feature_extractor import FeatureExtractor
from core_logic.feature_bank_manager import FeatureBankManager
from core_logic.decision_engine import DecisionEngine
from storage.file_router import FileAndDBRouter
from core_logic.video_processor import VideoProcessor

logging.basicConfig(level=logging.INFO, format='%(asctime)s - [S2-S3] - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

QUEUE_NAME = "ig_processing_queue"

class AIConsumer:
    def __init__(self):
        # 🌟 3. 嚴格初始化順序：Router -> Extractor -> BankManager -> VideoProcessor
        self.router = FileAndDBRouter(
            db_host=os.getenv('DB_HOST'),
            db_port=os.getenv('DB_PORT'),
            db_user=os.getenv('DB_USER'),
            db_password=os.getenv('DB_PASSWORD'),
            db_name=os.getenv('DB_NAME')
        )
        
        self.extractor = FeatureExtractor(
            detector_backend="retinaface" if "linux" in sys.platform else "mtcnn"
        )
        
        self.bank_manager = FeatureBankManager(
            router=self.router, 
            detector_backend=self.extractor.detector_backend
        )

        self.video_processor = VideoProcessor(
            extractor=self.extractor,
            router=self.router,
            bank_manager=self.bank_manager
        )
        
        self.redis = self._connect_redis()

    def _connect_redis(self):
        pool = redis.ConnectionPool(
            host=os.getenv('REDIS_HOST', 'localhost'),
            port=int(os.getenv('REDIS_PORT', 6379)),
            password=os.getenv('REDIS_PASSWORD', ''),
            decode_responses=True
        )
        return redis.Redis(connection_pool=pool)

    def _is_cold_start(self, profile: str) -> bool:
        """檢查 Qdrant 內是否已有該人物的正樣本"""
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
        """圖片專用路由：在 S3 內進行搬移並更新 DB"""
        if not media_id: return

        raw_db_path = self.router.get_real_db_path(media_id)
        if not raw_db_path: return

        # 將 DB 中的 s3:// 絕對路徑轉為相對 Object Key
        bucket_prefix = f"s3://{self.router.bucket_name}/"
        main_s3_key = raw_db_path.replace(bucket_prefix, "") if raw_db_path.startswith(bucket_prefix) else raw_db_path

        # 🌟 執行實體搬移 (僅針對圖片)
        new_main_key = self.router.move_file_safe(main_s3_key, target_folder)
        
        if new_main_key:
            self.router.update_media_asset_path(media_id, new_main_key)
            self.router.update_db_log(media_id, status, face_detected, score)
            
            try:
                self.redis.publish("ai_task_completed", "done")
            except: pass

    def process_task(self, task_json: str):
        task = json.loads(task_json)
        
        profile = task.get("profile") or task.get("system_name")
        stage = task.get("stage")
        s3_uri = task.get('file_path')
        media_id = task.get('task_id') or task.get('media_id')

        # 冷啟動觸發的特徵庫批次重建指令
        if task.get("type") == "BUILD_FEATURE_BANK":
            logger.info(f"🛠️ [系統指令] 收到冷啟動指令，重建特徵庫: {profile}")
            self.bank_manager.build_feature_bank(profile)
            return

        # 智能補全：若 Payload 僅有 media_id，則從資料庫抓回完整資訊
        if media_id and (not s3_uri or not profile):
            conn = self.router._get_connection()
            try:
                with conn.cursor() as cursor:
                    cursor.execute("SELECT file_path, system_name FROM media_assets WHERE id = %s", (media_id,))
                    res = cursor.fetchone()
                    if res:
                        s3_uri = s3_uri or res['file_path']
                        profile = profile or res['system_name']
                        logger.info(f"🔄 智能補全任務資訊: {profile} -> {s3_uri}")
            except Exception as e:
                logger.error(f"❌ 查詢完整資訊失敗: {e}")
            finally:
                conn.close()

        if not s3_uri or not media_id or not profile:
            logger.warning(f"⚠️ 任務資料不完整: {task}")
            return

        # 濾掉 s3:// 前綴
        bucket_prefix = f"s3://{self.router.bucket_name}/"
        s3_key = s3_uri.replace(bucket_prefix, "") if s3_uri.startswith(bucket_prefix) else s3_uri
        
        # ==========================================
        # 🌟 4. 長影片處理路由 (符合您的新需求：不移動檔案、不刪除)
        # ==========================================
        if s3_key.lower().endswith(".mp4"):
            logger.info(f"📽️ 偵測到影片格式，轉交長影片處理引擎: {s3_key}")
            db_threshold = self.router.get_person_threshold(profile)
            
            # 委派給 VideoProcessor，由其內部自行判斷 OUTPUT 或 PENDING 並更新 Log
            self.video_processor.process_long_video(
                media_id=media_id, 
                system_name=profile, 
                s3_key=s3_key, 
                db_threshold=db_threshold,
                sample_rate_sec=2.0
            )
            return # 影片處理完畢後立即結束，絕不觸發下方的圖片處理邏輯

        # ==========================================
        # 🌟 5. 靜態圖片處理邏輯
        # ==========================================
        stream = self.router.get_object_stream(s3_key)
        if not stream:
            logger.warning(f"❌ S3 影像不存在: {s3_key}")
            self.router.update_db_log(media_id, "ERROR", False, 0.0)
            return

        img_array = cv2.imdecode(np.frombuffer(stream.read(), np.uint8), cv2.IMREAD_COLOR)
        if img_array is None:
            logger.error(f"❌ OpenCV 解碼失敗: {s3_key}")
            self.router.update_db_log(media_id, "ERROR", False, 0.0)
            return

        # 手動上傳的「防呆與校驗」
        if stage == "MANUAL_UPLOAD":
            logger.info(f"🛡️ [嚴謹校驗] 開始檢查手動上傳的基準圖: {s3_key}")
            target_embedding, face_detected = self.extractor.extract_target_embedding(img_array)
            
            if not face_detected:
                logger.error(f"❌ [嚴謹校驗失敗] 照片中找不到人臉！")
                self.router.update_db_log(media_id, "REJECTED_NO_FACE", False, 0.0)
                self.router.move_file_safe(s3_key, "GARBAGE")
                return
            
            self.bank_manager.build_feature_bank(profile)
            self.router.update_db_log(media_id, "HITL_CONFIRMED", True, 1.0)
            return

        # 一般爬蟲照片處理
        target_embedding, face_detected = self.extractor.extract_target_embedding(img_array)
        if not face_detected:
            self._route_and_log(media_id, profile, s3_key, "NOFACE", "NOFACE", False)
            return

        if self._is_cold_start(profile):
            self._route_and_log(media_id, profile, s3_key, "INITIAL_REVIEW", "INITIAL_REVIEW", True)
            return

        db_threshold = self.router.get_person_threshold(profile)
        result, score, target_folder = DecisionEngine.compare_face_logic(
            target_embedding=target_embedding, 
            system_name=profile,
            qdrant_client=self.bank_manager.qdrant,
            db_threshold=db_threshold
        )

        status_map = {"MATCH_VSTACK": "OUTPUT", "PENDING": "PENDING", "GARBAGE": "REJECTED"}
        self._route_and_log(media_id, profile, s3_key, target_folder, status_map.get(result, "SKIP"), True, score)

    def run(self, max_workers=2):
        """啟動 AI 消費者，並使用執行緒池進行多任務併發處理"""
        logger.info(f"🚀 S2 AI Consumer 啟動 - 多執行緒 {max_workers} 人模式")
        queues = ["ig_processing_queue_high", "ig_processing_queue"]
        
        with ThreadPoolExecutor(max_workers=max_workers) as executor:
            while True:
                if not (result := self.redis.brpop(queues, timeout=10)):
                    continue
                
                _, task_json = result
                executor.submit(self._safe_execute, task_json)

    def _safe_execute(self, task_json):
        try:
            self.process_task(task_json)
        except Exception as e:
            logger.error(f"❌ 任務崩潰: {e}")

if __name__ == "__main__":
    consumer = AIConsumer()
    consumer.run(max_workers=2)