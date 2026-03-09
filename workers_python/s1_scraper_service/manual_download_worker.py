import os
import sys
import json
import time
import logging
from pathlib import Path
from dotenv import load_dotenv
import redis
import instaloader
import boto3
from botocore.config import Config
import requests

# 解決跨資料夾 import 問題
script_dir = Path(__file__).resolve().parent
workers_path = str(script_dir.parent)
if workers_path not in sys.path:
    sys.path.insert(0, workers_path)

from state_management.db_repository import DBRepository

# 載入環境變數
env_path = script_dir.parent.parent / '.env'
load_dotenv(dotenv_path=env_path)

logging.basicConfig(level=logging.INFO, format='%(asctime)s - [Manual-Worker] - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class ManualDownloadWorker:
    def __init__(self):
        logger.info("啟動隨選下載消費機 (On-Demand Download Worker)...")
        
        # 1. 建立 Redis 連線
        self.redis_client = redis.Redis(
            host=os.getenv('REDIS_HOST', 'localhost'),
            port=int(os.getenv('REDIS_PORT', 6379)),
            password=os.getenv('REDIS_PASSWORD', ''),
            decode_responses=True
        )
        
        # 2. 建立資料庫連線
        self.db_repo = DBRepository()
        
        # 3. 直接使用 boto3 連線至 MinIO
        minio_endpoint = os.getenv('MINIO_ENDPOINT', 'localhost:9000')
        endpoint_url = f"https://{minio_endpoint}" if os.getenv('MINIO_USE_SSL', 'false').lower() == 'true' else f"http://{minio_endpoint}"
        
        self.s3_client = boto3.client(
            's3',
            endpoint_url=endpoint_url,
            aws_access_key_id=os.getenv('MINIO_ACCESS_KEY', 'minioadmin'),
            aws_secret_access_key=os.getenv('MINIO_SECRET_KEY', 'minioadmin'),
            config=Config(signature_version='s3v4'),
            region_name='us-east-1'
        )
        self.bucket_name = "ig-ai-assets"
        
        # 4. 初始化 Instaloader
        self.L = instaloader.Instaloader(
            download_video_thumbnails=False,
            save_metadata=False,
            post_metadata_txt_pattern=''
        )
        
        self.source_queue = "ig_manual_download_queue"
        self.target_ai_queue = "ig_processing_queue"

    def run(self):
        logger.info(f"🎧 開始監聽 Redis 佇列: {self.source_queue}")
        while True:
            try:
                task_data = self.redis_client.blpop(self.source_queue, timeout=0)
                if not task_data:
                    continue
                
                queue_name, payload_str = task_data
                payload = json.loads(payload_str)
                
                media_id = payload.get("media_id")
                shortcode = payload.get("shortcode")
                
                if media_id and shortcode:
                    self._process_download(media_id, shortcode)

            except Exception as e:
                logger.error(f"❌ 處理排程時發生未預期錯誤: {str(e)}")
                time.sleep(5) 

    def _get_system_name(self, media_id: int) -> str:
        """🌟 智能補全：從 DB 透過 media_id 反查對應的 system_name"""
        query = "SELECT system_name FROM media_assets WHERE id = %s"
        conn = self.db_repo.pool.get_connection()
        try:
            with conn.cursor() as cursor:
                cursor.execute(query, (media_id,))
                res = cursor.fetchone()
                return res['system_name'] if res else "unknown"
        finally:
            conn.close()

    def _process_download(self, media_id: int, shortcode: str):
        logger.info(f"⬇️ 開始執行隨選下載 - MediaID: {media_id}, Shortcode: {shortcode}")
        
        try:
            # 🌟 1. 取得該資產所屬的人物系統名稱
            system_name = self._get_system_name(media_id)

            # 2. 透過 Shortcode 取得 IG 實體資料
            post = instaloader.Post.from_shortcode(self.L.context, shortcode)
            
            # 3. 準備檔案名稱與路徑 (加入 system_name 前綴)
            content_type = "video/mp4" if post.is_video else "image/jpeg"
            file_name = f"repost_approved_{shortcode}.mp4" if post.is_video else f"repost_approved_{shortcode}.jpg"
            target_key = f"{system_name}/official/{file_name}"
            
            # 4. 下載主體並上傳至 S3
            main_url = post.video_url if post.is_video else post.url
            media_bytes = self._download_to_memory(main_url)
            self.s3_client.put_object(
                Bucket=self.bucket_name,
                Key=target_key,
                Body=media_bytes,
                ContentType=content_type
            )

            # 🌟 5. 核心修正：如果是影片，額外下載封面圖供 AI 辨識使用
            if post.is_video:
                thumb_bytes = self._download_to_memory(post.url) # post.url 為影片封面圖
                thumb_name = f"repost_approved_{shortcode}.jpg"
                thumb_key = f"{system_name}/official/{thumb_name}"
                self.s3_client.put_object(
                    Bucket=self.bucket_name,
                    Key=thumb_key,
                    Body=thumb_bytes,
                    ContentType="image/jpeg"
                )
            downloaded_id = self.db.status_manager.get_id("DOWNLOAD_STATUS", "DOWNLOADED")
            # 🌟 6. 更新 MySQL (直接存入 target_key，不再帶有 s3:// 前綴)
            self._update_db_status(media_id, target_key, downloaded_id)
            
            # 7. 推送給 S2 AI 引擎進行特徵分析
            self.redis_client.lpush(self.target_ai_queue, json.dumps({"media_id": media_id}))
            logger.info(f"✅ 隨選下載完成，已交棒給 AI 引擎處理 (MediaID: {media_id})")

        except instaloader.exceptions.BadResponseException:
            logger.warning(f"⚠️ 找不到該貼文 [{shortcode}]，可能在審核期間已被原作者刪除。")
            self._update_db_status(media_id, None, 27) # 27: FAILED
        except Exception as e:
            logger.error(f"❌ 下載貼文 [{shortcode}] 失敗: {str(e)}")
            self._update_db_status(media_id, None, 27)

    def _download_to_memory(self, url: str) -> bytes:
        """實作 Requests 下載邏輯，將網路串流直接載入 RAM"""
        response = requests.get(url, timeout=15)
        response.raise_for_status()
        return response.content

    def _update_db_status(self, media_id: int, new_file_path: str, status_id: int):
        """利用現有的 DBRepository 更新資料庫狀態與路徑"""
        query = """
            UPDATE media_assets 
            SET download_status_id = %s, updated_at = NOW()
            """
        params = [status_id]
        
        if new_file_path:
            query = """
            UPDATE media_assets 
            SET download_status_id = %s, file_path = %s 
            WHERE id = %s
            """
            params.extend([new_file_path, media_id])
        else:
            query += " WHERE id = %s"
            params.append(media_id)

        conn = self.db_repo.pool.get_connection()
        try:
            with conn.cursor() as cursor:
                cursor.execute(query, tuple(params))
                conn.commit()
        finally:
            conn.close()

if __name__ == "__main__":
    worker = ManualDownloadWorker()
    worker.run()