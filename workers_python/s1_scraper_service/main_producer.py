import os
import sys
import time
import random
import logging
import shutil
from pathlib import Path
from dotenv import load_dotenv

# 匯入原始模組
from clients.ig_client import IGClient
from clients.redis_publisher import RedisPublisher
from state_management.db_repository import DBRepository
from state_management.checkpoint_tracker import CheckpointTracker
from utils.media_ffmpeg import MediaProcessor
script_dir = Path(__file__).resolve().parent
# 假設 main_producer.py 在 workers_python/s1_producer/ 之下，向上推兩層就是根目錄
root_path = str(script_dir.parent.parent)

if root_path not in sys.path:
    sys.path.insert(0, root_path)

# 🌟 匯入新版 S3 路由 (請確保 workers_python 相關路徑已加入 sys.path)
from workers_python.s2_ai_consumer_service.storage.file_router import FileAndDBRouter

# 環境變數與日誌設定
script_dir = Path(__file__).resolve().parent
env_path = script_dir.parent.parent / '.env'
load_dotenv(dotenv_path=env_path)

logging.basicConfig(level=logging.INFO, format='%(asctime)s - [S1-Cloud] - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class S1Producer:
    def __init__(self):
        self.base_storage_path = script_dir.parent.parent / "temp_download"
        if not self.base_storage_path.exists():
            os.makedirs(self.base_storage_path)

        # 🌟 直接使用你寫好的 Router
        self.router = FileAndDBRouter(
            db_host=os.getenv('DB_HOST'),
            db_port=os.getenv('DB_PORT'),
            db_user=os.getenv('DB_USER'),
            db_password=os.getenv('DB_PASSWORD'),
            db_name=os.getenv('DB_NAME')
        )

        # 初始化原始模組
        self.ig = IGClient(self.base_storage_path)
        self.redis = RedisPublisher()
        self.db = DBRepository()
        self.checkpoints = CheckpointTracker(self.base_storage_path)

        if not self.ig.initialize_login():
            sys.exit(1)

    def process_media_pipeline(self, post, system_name: str, is_story: bool = False):
        """[完全保留原始邏輯] 下載並執行影音處理管線 (ffmpeg)"""
        media_info = self.ig.download_media(post, system_name, is_story)
        if not media_info: 
            return None, None, None, None

        filename = media_info["filename"]
        video_file = media_info["video_file"]
        image_file = media_info["image_file"]
        media_type = "VIDEO" if media_info["is_video"] else "IMAGE"
        
        db_file_path = image_file
        ai_file_path = image_file
        db_filename = f"{filename}.jpg"

        # 🛑 核心邏輯保留：如果是影片，執行合併與靜態判定
        if media_type == "VIDEO" and os.path.exists(video_file):
            MediaProcessor.merge_thumbnail_to_video(video_file, image_file)
            
            if MediaProcessor.is_video_static(video_file):
                logger.info(f"✂️ 移除靜態影片: {filename}")
                try: os.remove(video_file)
                except: pass
                media_type = "IMAGE"
            else:
                db_file_path = video_file
                db_filename = f"{filename}.mp4"

        if os.path.exists(db_file_path) and os.path.exists(ai_file_path):
            return db_file_path, ai_file_path, db_filename, media_type
        return None, None, None, None

    def process_one_account(self, system_name: str, account: str, current_whitelist: dict):
        """[完全保留原始邏輯] 處理單一帳號的所有邏輯 (限動 + 貼文)"""
        logger.info(f"\n🔍 開始掃描: {account} (@{system_name})")
        
        # 確保本地暫存目錄存在
        downloads_dir = self.base_storage_path / system_name / "downloads"
        downloads_dir.mkdir(parents=True, exist_ok=True)

        try:
            profile = self.ig.get_profile(account)

            # --- Stories (限動) ---
            if profile.has_public_story:
                logger.info("📸 檢查限動...")
                for story in self.ig.L.get_stories(userids=[profile.userid]):
                    for item in story.get_items():
                        self.redis.throttle_if_queue_full(max_size=3)
                        
                        if self.ig.is_repost(item, is_story=True, system_name=system_name):
                            logger.info(f"⏭️ 跳過限動轉發: {item.date_local}")
                            continue

                        db_path, ai_path, fn, mt = self.process_media_pipeline(item, system_name, is_story=True)
                        if db_path:
                            # 🌟 調用改寫後的 S3 派發邏輯
                            self._save_and_dispatch(system_name, account, fn, db_path, ai_path, mt, "STORY")

            # --- Posts (貼文) ---
            logger.info("📄 檢查貼文...")
            checkpoint = self.checkpoints.get_checkpoint(system_name, account)
            new_checkpoint = None
            is_first_post = True
            scan_completed = False

            for post in self.ig.safe_generator(profile.get_posts()):
                self.redis.throttle_if_queue_full(max_size=3)

                # 🛑 原始邏輯：置頂檢查
                if self.checkpoints.is_post_pinned_safe(post) or post.shortcode in self.checkpoints.pinned_posts.get(account, []):
                    self.checkpoints.update_pinned_list(account, post.shortcode)
                    logger.info(f"📌 跳過置頂: {post.shortcode}")
                    continue

                # 🛑 原始邏輯：轉發過濾
                if self.ig.is_repost(post, is_story=False, target_username=account, system_name=system_name, current_whitelist=current_whitelist):
                    logger.info(f"⏭️ 跳過轉發/非本人貼文 (Owner: {post.owner_username})")
                    continue

                # 🛑 原始邏輯：檢查點判定
                if is_first_post:
                    new_checkpoint = post.shortcode
                    is_first_post = False

                if checkpoint and post.shortcode == checkpoint:
                    logger.info(f"✓ 到達檢查點 {checkpoint}")
                    scan_completed = True
                    break

                # 🛑 原始邏輯：下載處理
                db_path, ai_path, fn, mt = self.process_media_pipeline(post, system_name)
                if db_path:
                    s_type = "REEL" if post.typename == "GraphVideo" else "POST"
                    # 🌟 調用改寫後的 S3 派發邏輯
                    self._save_and_dispatch(system_name, account, fn, db_path, ai_path, mt, s_type)

                time.sleep(random.uniform(2, 5))

            # 🛑 原始邏輯：交易式提交檢查點
            if new_checkpoint and (scan_completed or checkpoint is None):
                self.checkpoints.save_checkpoint(system_name, account, new_checkpoint)
            else:
                logger.warning("⚠️ 掃描未完整銜接，不更新檢查點。")

        except Exception as e:
            logger.error(f"❌ 帳號處理異常: {e}")

    def _save_and_dispatch(self, system_name, account, fn, db_path, ai_path, mt, s_type):
        """🌟 確保主檔案與 AI 檔案同步上傳，並清理本地"""
        s3_key_main = f"{system_name}/downloads/{fn}" 
        s3_key_ai = f"{system_name}/downloads/{os.path.basename(ai_path)}" 

        # 🌟 產生全域通用的 S3 URI
        s3_uri_main = f"s3://{self.router.bucket_name}/{s3_key_main}"
        s3_uri_ai = f"s3://{self.router.bucket_name}/{s3_key_ai}"

        # 1. 透過 Router 上傳主檔案
        if not self.router.upload_file(db_path, s3_key_main): 
            return

        # 2. 如果是影片，額外上傳縮圖
        if ai_path != db_path:
            self.router.upload_file(ai_path, s3_key_ai)

        # 3. 寫入 DB (強制存入 s3_uri_main)
        media_id, is_zombie = self.db.insert_media_record(system_name, account, fn, s3_uri_main, mt, s_type)
        
        if media_id is not None:
            # 4. Redis 任務發送 S3 URI
            payload = {
                "task_id": media_id, 
                "profile": system_name, 
                "file_path": s3_uri_ai,        
                "main_file_path": s3_uri_main, 
                "media_type": mt,
                "stage": "S1_S3_UPLOADED"
            }
            self.redis.push_task(payload, is_priority=(mt == "IMAGE"))

        # 5. 檔案不落地：清理暫存
        try:
            if os.path.exists(db_path): os.remove(db_path)
            if ai_path != db_path and os.path.exists(ai_path): os.remove(ai_path)
            
            parent_dir = os.path.dirname(db_path)
            if os.path.exists(parent_dir) and not os.listdir(parent_dir):
                shutil.rmtree(parent_dir, ignore_errors=True)
        except Exception as e:
            logger.error(f"⚠️ 暫存清理失敗: {e}")

    def watchdog_orphaned_downloads(self, profiles):
        """[雲端改寫版] 掃描 S3 孤兒檔案並重新推進 Redis"""
        logger.info("🐕 啟動 Watchdog：掃描 S3 下載區...")
        current_time = time.time()
        recovered_count = 0
        
        queue_length = self.redis.get_queue_length()
        # 🛑 原始邏輯：動態緩衝
        buffer_seconds = 10 if queue_length == 0 else 1800
        
        for system_name in profiles.keys():
            # 🌟 向 S3 詢問該人物的下載區
            prefix = f"{system_name}/downloads/"
            try:
                response = self.router.s3_client.list_objects_v2(
                    Bucket=self.router.bucket_name, Prefix=prefix
                )
                
                if 'Contents' not in response: continue

                for obj in response['Contents']:
                    s3_key = obj['Key']
                    filename = os.path.basename(s3_key)
                    if not filename: continue

                    # 🌟 檢查 S3 檔案時間戳記
                    last_modified = obj['LastModified'].timestamp()
                    file_age = current_time - last_modified
                    
                    if file_age > buffer_seconds:
                        payload = {
                            "profile": system_name,
                            "file_path": s3_key,
                            "timestamp": current_time,
                            "stage": "S1_WATCHDOG_S3_RECOVERED",
                            "reprocess": True
                        }
                        
                        is_priority = not filename.lower().endswith('.mp4')
                        self.redis.push_task(payload, is_priority=is_priority)
                        recovered_count += 1
            except Exception as e:
                logger.error(f"⚠️ Watchdog S3 掃描失敗: {e}")

        if recovered_count > 0:
            logger.info(f"✅ Watchdog 完成，重新塞入 {recovered_count} 個 S3 任務。")
        else:
            logger.info("✅ Watchdog 完成，S3 downloads 目錄健康。")

    def run_random_test(self):
        """[完全保留原始邏輯] 隨機測試"""
        logger.info("🧪 啟動 S1 Producer [隨機單次測試模式]...")
        profiles = self.db.get_dynamic_profiles()
        whitelist = self.db.get_dynamic_whitelist()
        
        all_targets = [(sys_name, acc) for sys_name, accs in profiles.items() for acc in accs]
        if not all_targets: return

        target_sys, target_acc = random.choice(all_targets)
        self.process_one_account(target_sys, target_acc, whitelist)

    def run(self):
        """[完全保留原始邏輯] 正式輪詢"""
        logger.info("🔥 啟動 S1 Producer [正式運行模式 - S3 雲端版]...")
        while True:
            profiles = self.db.get_dynamic_profiles()
            whitelist = self.db.get_dynamic_whitelist()
            
            for system_name, accounts in profiles.items():
                for account in accounts:
                    self.process_one_account(system_name, account, whitelist)

            try:
                self.watchdog_orphaned_downloads(profiles)
            except Exception as e:
                logger.error(f"❌ Watchdog 巡檢異常: {e}")

            logger.info("💤 本輪結束，休息 15 分鐘...")
            time.sleep(900)

if __name__ == "__main__":
    producer = S1Producer()
    # 預設執行單次隨機測試，若要跑正式模式請改為 producer.run()
    producer.run_random_test()