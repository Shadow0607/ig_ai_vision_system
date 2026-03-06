import os
import cv2
import json
import time
import logging
import tempfile
import numpy as np
import boto3
import redis
import mysql.connector
from mysql.connector import pooling

# 🌟 1. 新增：引入路徑解析與環境變數載入模組
from pathlib import Path
from dotenv import load_dotenv

# 🌟 2. 核心修正：動態定位專案根目錄的 .env 檔案
# __file__ 位於 workers_python/s5_story_feedback.py
script_dir = Path(__file__).resolve().parent
# 專案根目錄 (ig_ai_vision_system)
root_path = script_dir.parent
env_path = root_path / '.env'

# 載入 .env
load_dotenv(dotenv_path=env_path)

# 系統日誌設定
logging.basicConfig(level=logging.INFO, format='%(asctime)s - [S5_FEEDBACK] - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class S5StoryFeedbackManager:
    def __init__(self):
        # 現在這裡的 os.getenv 就能完美讀取到 .env 內的真實帳密了！
        self._init_db_pool()
        self._init_redis()
        self._init_s3_client()
        
        # S3 Bucket 名稱設定
        self.bucket_name = os.getenv('S3_BUCKET_NAME', 'ig-ai-assets')
        
        # 定義狀態字典 (預設依據最新 DB Schema ID)
        self.STATUS = {
            'MEDIA_TYPE_IMAGE': 34,
            'MEDIA_TYPE_VIDEO': 35,
            'STATIC_FAKE_VIDEO': 40
        }

    def _init_db_pool(self):
        """初始化 MySQL 連線池"""
        self.db_pool = mysql.connector.pooling.MySQLConnectionPool(
            pool_name="s5_pool",
            pool_size=5,
            pool_reset_session=True,
            host=os.getenv('DB_HOST', 'localhost'),
            port=int(os.getenv('DB_PORT', 3306)),
            user=os.getenv('DB_USER', 'root'),
            password=os.getenv('DB_PASSWORD', ''),
            database=os.getenv('DB_NAME', 'ig_ai_system')
        )

    def _init_redis(self):
        """初始化 Redis 連線"""
        self.redis_client = redis.Redis(
            host=os.getenv('REDIS_HOST', 'localhost'),
            port=int(os.getenv('REDIS_PORT', 6379)),
            password=os.getenv('REDIS_PASSWORD', ''),
            decode_responses=True
        )

    def _init_s3_client(self):
        """初始化 S3/MinIO 客戶端"""
        self.s3_client = boto3.client(
            's3',
            endpoint_url=os.getenv('S3_ENDPOINT_URL'),
            aws_access_key_id=os.getenv('S3_ACCESS_KEY'),
            aws_secret_access_key=os.getenv('S3_SECRET_KEY'),
            region_name=os.getenv('S3_REGION', 'us-east-1')
        )

    def fetch_unprocessed_videos(self):
        """從資料庫撈取尚未檢測的影片紀錄"""
        conn = self.db_pool.get_connection()
        try:
            with conn.cursor(dictionary=True) as cursor:
                # 撈取 media_type_id 為影片(35)，且尚未被標記為假影片的紀錄
                query = """
                    SELECT id, file_name, file_path, system_name, account_id 
                    FROM media_assets 
                    WHERE media_type_id = %s 
                    AND download_status_id != %s
                    ORDER BY created_at DESC LIMIT 50
                """
                cursor.execute(query, (self.STATUS['MEDIA_TYPE_VIDEO'], self.STATUS['STATIC_FAKE_VIDEO']))
                return cursor.fetchall()
        finally:
            conn.close()

    def calculate_mse(self, imageA, imageB):
        # MSE 是兩張圖差異的平方和
        err = np.sum((imageA.astype("float") - imageB.astype("float")) ** 2)
        err /= float(imageA.shape[0] * imageA.shape[1])
        return err

    def is_static_fake_video(self, local_video_path):
        """透過 OpenCV 判斷是否為靜態假影片，並回傳第一幀圖片"""
        cap = cv2.VideoCapture(local_video_path)
        if not cap.isOpened():
            logger.error(f"❌ 無法開啟影片: {local_video_path}")
            return False, None

        # 讀取第一影格
        ret, first_frame = cap.read()
        if not ret or first_frame is None:
            logger.error(f"❌ 讀取第一影格失敗: {local_video_path}")
            cap.release()
            return False, None

        # 獲取總幀數
        total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
        
        # 防呆：如果 OpenCV 抓不到總幀數，這通常是影片索引壞了
        if total_frames <= 0:
            logger.warning(f"⚠️ 影片總幀數異常 ({total_frames})，視為一般影片處理")
            cap.release()
            return False, first_frame

        if total_frames < 10: 
            cap.release()
            return False, first_frame 

        # 跳轉到 80% 處
        target_frame_idx = int(total_frames * 0.8)
        cap.set(cv2.CAP_PROP_POS_FRAMES, target_frame_idx)
        ret, nth_frame = cap.read()
        cap.release() # 讀完就放掉資源

        # 🌟 核心修正：如果 80% 處讀不到，不要繼續執行 cv2.cvtColor
        if not ret or nth_frame is None:
            logger.warning(f"⚠️ 無法讀取第 {target_frame_idx} 影格，跳過靜態檢測")
            return False, first_frame

        try:
            # 轉為灰階計算 MSE
            gray1 = cv2.cvtColor(first_frame, cv2.COLOR_BGR2GRAY)
            grayN = cv2.cvtColor(nth_frame, cv2.COLOR_BGR2GRAY)
            
            # 🌟 確保兩張圖的大小一致，否則 MSE 也會報錯 (tuple index out of range)
            if gray1.shape != grayN.shape:
                logger.warning("⚠️ 影格尺寸不一致，跳過檢測")
                return False, first_frame

            mse = self.calculate_mse(gray1, grayN)
            logger.info(f"影片動態 MSE 差異值: {mse:.2f}")

            # 若 MSE 極低，判定為靜態假影片
            is_static = mse < 10.0
            return is_static, first_frame
        except Exception as e:
            logger.error(f"❌ MSE 計算過程發生錯誤: {e}")
            return False, first_frame

    def downgrade_and_requeue(self, asset, first_frame):
        """執行型態降級、瘦身並重新發佈到 AI 佇列"""
        old_s3_key = asset['file_path'].replace(f"s3://{self.bucket_name}/", "")
        new_file_name = asset['file_name'].replace('.mp4', '.jpg')
        new_s3_key = old_s3_key.replace('.mp4', '.jpg')

        try:
            # 1. 將第一幀 JPG 寫入 S3 (取代 MP4)
            _, buffer = cv2.imencode('.jpg', first_frame, [int(cv2.IMWRITE_JPEG_QUALITY), 95])
            self.s3_client.put_object(
                Bucket=self.bucket_name,
                Key=new_s3_key,
                Body=buffer.tobytes(),
                ContentType='image/jpeg'
            )

            # 2. 刪除原本龐大的 MP4
            self.s3_client.delete_object(Bucket=self.bucket_name, Key=old_s3_key)
            logger.info(f"✅ 已刪除實體影片並轉換為封面圖: {new_s3_key}")

            # 3. 更新 MySQL 資料庫 (Transaction 保證一致性)
            conn = self.db_pool.get_connection()
            try:
                with conn.cursor() as cursor:
                    update_sql = """
                        UPDATE media_assets 
                        SET media_type_id = %s, download_status_id = %s, file_name = %s, file_path = %s
                        WHERE id = %s
                    """
                    cursor.execute(update_sql, (
                        self.STATUS['MEDIA_TYPE_IMAGE'], 
                        self.STATUS['STATIC_FAKE_VIDEO'], 
                        new_file_name, 
                        f"s3://{self.bucket_name}/{new_s3_key}",
                        asset['id']
                    ))
                    conn.commit()
            finally:
                conn.close()

            # 4. 資料飛輪：重新推入 S2 Redis 佇列，讓 AI 重新萃取特徵
            task_payload = {
                "media_id": asset['id'],
                "system_name": asset['system_name'],
                "file_path": f"s3://{self.bucket_name}/{new_s3_key}"
            }
            self.redis_client.lpush("ig_processing_queue", json.dumps(task_payload))
            logger.info(f"🔄 資料飛輪已啟動：{new_file_name} 已重新排入 AI 萃取佇列。")

        except Exception as e:
            logger.error(f"❌ 處理降級與回推失敗: {e}")

    def run(self):
        """執行 S5 背景排程守護進程"""
        logger.info("🚀 S5 智能反饋與清理飛輪啟動中...")
        while True:
            try:
                videos = self.fetch_unprocessed_videos()
                for asset in videos:
                    s3_key = asset['file_path'].replace(f"s3://{self.bucket_name}/", "")
                    
                    # 雲原生無狀態處理：下載至暫存檔，處理完自動銷毀
                    with tempfile.NamedTemporaryFile(suffix='.mp4', delete=True) as temp_vid:
                        logger.info(f"正在分析影片: {asset['file_name']}")
                        self.s3_client.download_file(self.bucket_name, s3_key, temp_vid.name)
                        
                        is_static, first_frame = self.is_static_fake_video(temp_vid.name)
                        
                        if is_static and first_frame is not None:
                            logger.info(f"⚠️ 偵測到靜態假影片: {asset['file_name']}，啟動降級瘦身程序。")
                            self.downgrade_and_requeue(asset, first_frame)
                        else:
                            # 即使是真影片，也可標記為已檢測 (避免重複撈取)，此處可依需求擴充狀態
                            pass

                # 排程休眠
                time.sleep(60 * 15) # 每 15 分鐘巡迴一次
            
            except Exception as e:
                logger.error(f"S5 飛輪執行發生未預期錯誤: {e}")
                time.sleep(60)

if __name__ == "__main__":
    manager = S5StoryFeedbackManager()
    manager.run()