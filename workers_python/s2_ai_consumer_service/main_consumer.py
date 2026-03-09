import os
import sys
import json
import redis
import logging
import cv2
import numpy as np
from pathlib import Path
script_dir = Path(__file__).resolve().parent
workers_path = str(script_dir.parent)       # 🌟 指向 workers_python 目錄
root_path = str(script_dir.parent.parent)   # 指向 ig_ai_vision_system 根目錄

# 將 workers_python 加入搜尋路徑 (解決 from shared 找不到的問題)
if workers_path not in sys.path:
    sys.path.insert(0, workers_path)

# 將根目錄加入搜尋路徑
if root_path not in sys.path:
    sys.path.insert(0, root_path)
    
from dotenv import load_dotenv
from io import BytesIO
from qdrant_client.http import models
from concurrent.futures import ThreadPoolExecutor
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
        
        # 🌟 1. 寬鬆接收各種 Payload 格式 (相容 S1 爬蟲、S5 飛輪與手動上傳)
        profile = task.get("profile") or task.get("system_name")
        stage = task.get("stage")
        s3_uri = task.get('file_path')
        media_id = task.get('task_id') or task.get('media_id')

        # ==========================================
        # 🌟 嚴謹處理 1：冷啟動觸發的特徵庫批次重建
        # ==========================================
        if task.get("type") == "BUILD_FEATURE_BANK":
            logger.info(f"🛠️ [系統指令] 收到冷啟動指令，重建特徵庫: {profile}")
            self.bank_manager.build_feature_bank(profile)
            return

        # ==========================================
        # 🌟 智能補全：如果只收到 media_id，主動向 MySQL 查詢完整資訊
        # ==========================================
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

        # 防呆攔截：如果查完資料庫還是缺件，才捨棄任務
        if not s3_uri or not media_id or not profile:
            logger.warning(f"⚠️ 任務資料不完整或資料庫查無紀錄: {task}")
            return

        # 濾掉 s3:// 前綴
        bucket_prefix = f"s3://{self.router.bucket_name}/"
        s3_key = s3_uri.replace(bucket_prefix, "") if s3_uri.startswith(bucket_prefix) else s3_uri
        
        # ==========================================
        # 🌟 核心修正：如果是影片，AI 必須讀取它的縮圖 (.jpg) 才能進行特徵提取
        # ==========================================
        ai_target_key = s3_key
        if s3_key.lower().endswith(".mp4"):
            ai_target_key = s3_key.rsplit('.', 1)[0] + ".jpg"

        # 讀取實體檔案串流 (改讀 ai_target_key)
        stream = self.router.get_object_stream(ai_target_key)
        if not stream:
            logger.warning(f"❌ S3 影像不存在: {ai_target_key}")
            # 🌟 修正：對齊資料庫現有的狀態碼 "ERROR"
            self.router.update_db_log(media_id, "ERROR", False, 0.0)
            return

        # 影像解碼 (現在保證一定是圖片的 Bytes)
        img_array = cv2.imdecode(np.frombuffer(stream.read(), np.uint8), cv2.IMREAD_COLOR)
        if img_array is None:
            logger.error(f"❌ OpenCV 解碼失敗: {ai_target_key}")
            # 🌟 修正：對齊資料庫現有的狀態碼 "ERROR"
            self.router.update_db_log(media_id, "ERROR", False, 0.0)
            return

        # ==========================================
        # 🌟 嚴謹處理 2：手動頭像上傳的「防呆與校驗」
        # ==========================================
        if stage == "MANUAL_UPLOAD":
            logger.info(f"🛡️ [嚴謹校驗] 開始檢查手動上傳的基準圖: {ai_target_key}")
            
            # 強制提取人臉特徵
            target_embedding, face_detected = self.extractor.extract_target_embedding(img_array)
            
            if not face_detected:
                logger.error(f"❌ [嚴謹校驗失敗] 手動上傳的照片中找不到人臉！攔截寫入。")
                self.router.update_db_log(media_id, "REJECTED_NO_FACE", False, 0.0)
                self.router.move_file_safe(s3_key, "GARBAGE")
                return
            
            logger.info(f"✅ [嚴謹校驗通過] 成功提取手動頭像特徵，寫入大腦！")
            self.bank_manager.build_feature_bank(profile)
            
            self.router.update_db_log(media_id, "HITL_CONFIRMED", True, 1.0)
            return

        # ==========================================
        # 一般爬蟲照片的標準處理流程
        # ==========================================
        target_embedding, face_detected = self.extractor.extract_target_embedding(img_array)
        if not face_detected:
            self._route_and_log(media_id, profile, s3_key, "NOFACE", "NOFACE", False)
            return

        if self._is_cold_start(profile):
            self._route_and_log(media_id, profile, s3_key, "INITIAL_REVIEW", "INITIAL_REVIEW", True)
            return

        # 呼叫純 Qdrant 決策引擎
        db_threshold = self.router.get_person_threshold(profile)
        result, score, target_folder = DecisionEngine.compare_face_logic(
            target_embedding=target_embedding, 
            system_name=profile,
            qdrant_client=self.bank_manager.qdrant,
            db_threshold=db_threshold
        )

        status_map = {"MATCH_VSTACK": "OUTPUT", "PENDING": "PENDING", "GARBAGE": "REJECTED"}
        self._route_and_log(media_id, profile, s3_key, target_folder, status_map.get(result, "SKIP"), True, score)

    def run(self, max_workers=10):
        """
        啟動 AI 消費者，並使用執行緒池進行多任務併發處理。
        :param max_workers: 同時開啟的 AI 工人數量 (預設 5 個)
        """
        logger.info(f"🚀 S2 AI Consumer 啟動 (純 S3 + Qdrant 雲端原生版) - 影分身 {max_workers} 人模式")
        queues = ["ig_processing_queue_high", "ig_processing_queue"]
        
        # 🌟 建立執行緒池
        with ThreadPoolExecutor(max_workers=max_workers) as executor:
            while True:
                # 這裡的主執行緒只負責一件事：當慣老闆，不斷從 Redis 拿任務
                if not (result := self.redis.brpop(queues, timeout=10)):
                    continue
                
                # 拿到任務後，立刻丟給空閒的工人去處理
                match result:
                    case ("ig_processing_queue_high", task_json):
                        logger.info("⚡ [HIGH PRIORITY] 派發高優先級任務給空閒工人")
                        executor.submit(self._safe_execute, task_json)
                    case (_, task_json):
                        executor.submit(self._safe_execute, task_json)

    def _safe_execute(self, task_json):
        """包裝任務執行，確保單一執行緒崩潰不會影響整個系統"""
        try:
            self.process_task(task_json)
        except Exception as e:
            logger.error(f"❌ 任務崩潰: {e}")

if __name__ == "__main__":
    consumer = AIConsumer()
    consumer.run(max_workers=2)