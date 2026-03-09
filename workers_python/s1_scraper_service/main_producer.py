import os
import sys
import time
import random
import logging
import io  # 🌟 引入記憶體串流
import json
from pathlib import Path
from dotenv import load_dotenv
import redis
script_dir = Path(__file__).resolve().parent
workers_path = str(script_dir.parent)       # 🌟 指向 workers_python 目錄
root_path = str(script_dir.parent.parent)   # 指向 ig_ai_vision_system 根目錄

# 將 workers_python 加入搜尋路徑 (解決 from shared 找不到的問題)
if workers_path not in sys.path:
    sys.path.insert(0, workers_path)

# 將根目錄加入搜尋路徑 (解決 from workers_python.s2_ai_consumer_service... 找不到的問題)
if root_path not in sys.path:
    sys.path.insert(0, root_path)
# 匯入原始模組 (請確保路徑正確)
from clients.ig_client import IGClient
from clients.redis_publisher import RedisPublisher
from state_management.db_repository import DBRepository
from state_management.checkpoint_tracker import CheckpointTracker
from utils.media_ffmpeg import MemoryMediaProcessor # 🌟 改用無狀態影音處理器

# 匯入新版 S3 路由
from workers_python.s2_ai_consumer_service.storage.file_router import FileAndDBRouter

# 環境變數與日誌設定
env_path = script_dir.parent.parent / '.env'
load_dotenv(dotenv_path=env_path)

logging.basicConfig(level=logging.INFO, format='%(asctime)s - [S1-Cloud] - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class S1Producer:
    def __init__(self):
        # ❌ 徹底移除本地硬碟路徑 self.base_storage_path = ...

        # 🌟 1. 初始化 S3/MinIO Router
        self.router = FileAndDBRouter(
            db_host=os.getenv('DB_HOST'),
            db_port=os.getenv('DB_PORT'),
            db_user=os.getenv('DB_USER'),
            db_password=os.getenv('DB_PASSWORD'),
            db_name=os.getenv('DB_NAME')
        )

        # 🌟 2. 初始化 IGClient (現在不需要傳入本地路徑，傳入 None 即可)
        self.ig = IGClient(None) 
        self.redis = RedisPublisher()
        self.db = DBRepository()
        
        # 🌟 3. 初始化全域 Redis 與 CheckpointTracker
        self.raw_redis = redis.Redis(
            host=os.getenv('REDIS_HOST', 'localhost'),
            port=int(os.getenv('REDIS_PORT', 6379)),
            password=os.getenv('REDIS_PASSWORD'),
            decode_responses=True
        )
        self.checkpoints = CheckpointTracker(self.raw_redis)

        if not self.ig.initialize_login():
            sys.exit(1)

    def process_media_pipeline(self, post, system_name: str, is_story: bool = False):
        """🌟 全記憶體處理管線 (Pipeline)"""
        # 注意：IGClient 必須實作 download_media_as_bytes，直接回傳位元組而不是存檔
        media_list = self.ig.download_media_as_bytes(post, system_name, is_story)
        if not media_list: return []

        valid_media = []
        for media_info in media_list:
            filename = media_info["filename"]
            video_bytes = media_info.get("video_bytes")
            image_bytes = media_info.get("image_bytes")
            media_type = "VIDEO" if media_info.get("is_video") else "IMAGE"
            
            main_bytes = image_bytes
            ai_bytes = image_bytes
            db_filename = f"{filename}.jpg"

            # 🌟 在記憶體中進行影片處理
            if media_type == "VIDEO" and video_bytes:
                # 靜態影片偵測 (不落地)
                if MemoryMediaProcessor.is_video_static_bytes(video_bytes):
                    logger.info(f"✂️ 移除靜態影片，降級為圖片: {filename}")
                    media_type = "IMAGE"
                else:
                    # 合併封面圖 (不落地)
                    if image_bytes:
                        video_bytes = MemoryMediaProcessor.merge_cover_to_video_bytes(video_bytes, image_bytes)
                    
                    main_bytes = video_bytes
                    ai_bytes = image_bytes # AI 掃描專用的輕量縮圖
                    db_filename = f"{filename}.mp4"

            if main_bytes:
                valid_media.append((main_bytes, ai_bytes, db_filename, media_type))
                
        return valid_media

    def process_one_account(self, system_name: str, account: str, current_whitelist: dict):
        logger.info(f"\n🔍 開始掃描: {account} (@{system_name})")

        try:
            profile = self.ig.get_profile(account)
            account_type_row = self.db.get_account_type(account)
            account_type_id = account_type_row[0] if account_type_row else 4
            is_trusted = profile.is_verified or (account_type_id in [1, 2, 3])
            if is_trusted:
                logger.info(f"🔰 帳號信任驗證通過：將啟用直通車模式")
            else:
                logger.info(f"🛡️ 帳號未受信任：所有下載將進入 PENDING 隔離區")

            # --- Stories (限動) ---
            if profile.has_public_story:
                logger.info("📸 檢查限動...")
                for story in self.ig.L.get_stories(userids=[profile.userid]):
                    for item in story.get_items():
                        self.redis.throttle_if_queue_full(max_size=200)
                        
                        if self.ig.is_repost(item, is_story=True, system_name=system_name):
                            logger.info(f"⏭️ 跳過限動轉發: {item.date_local}")
                            continue

                        results = self.process_media_pipeline(item, system_name, is_story=True)
                        for main_bytes, ai_bytes, fn, mt in results:
                            # 🌟 核心智能分流：有藍勾勾 -> 直通車(DOWNLOADED)，沒有 -> 隔離(PENDING)
                            target_status = "DOWNLOADED" if is_trusted else "PENDING"
                            
                            # 🌟 新增：依據狀態動態決定 S3 的存放資料夾
                            target_folder = "official" if is_trusted else "quarantine"
                            
                            asset_data = {
                                "source_type_id": self.db.status_manager.get_id("SOURCE_TYPE", "STORY"),
                                "download_status_id": self.db.status_manager.get_id("DOWNLOAD_STATUS", target_status),
                                "original_username": account,
                                "original_shortcode": getattr(item, 'shortcode', str(item.mediaid)), # 防呆: 避免部分限動沒有 shortcode 屬性
                                "source_is_verified": int(profile.is_verified)
                            }
                            
                            # 👇 最後一個參數改為動態的 target_folder
                            self._save_and_dispatch_memory(system_name, fn, main_bytes, ai_bytes, asset_data, target_folder)

            # --- Posts (貼文) ---
            logger.info("📄 檢查貼文...")
            checkpoint = self.checkpoints.get_checkpoint(system_name, account)
            new_checkpoint = None
            is_first_post = True
            scan_completed = False

            for post in self.ig.safe_generator(profile.get_posts()):
                self.redis.throttle_if_queue_full(max_size=200)

                # 🌟 使用新的 Redis Set 置頂檢查邏輯 (支援多容器併發)
                if self.checkpoints.is_post_pinned_safe(post) or self.checkpoints.is_already_pinned(account, post.shortcode):
                    self.checkpoints.update_pinned_list(account, post.shortcode)
                    logger.info(f"📌 跳過置頂: {post.shortcode}")
                    continue

                if self.ig.is_repost(post, is_story=False, target_username=account, system_name=system_name, current_whitelist=current_whitelist):
                    logger.info(f"⏭️ 跳過轉發/非本人貼文 (Owner: {post.owner_username})")
                    continue

                if is_first_post:
                    new_checkpoint = post.shortcode
                    is_first_post = False

                if checkpoint and post.shortcode == checkpoint:
                    logger.info(f"✓ 到達檢查點 {checkpoint}")
                    scan_completed = True
                    break
                logger.info(f"⬇️ 開始下載與處理貼文: {post.shortcode} ...")

                results = self.process_media_pipeline(post, system_name)
                results = self.process_media_pipeline(post, system_name)
                for main_bytes, ai_bytes, fn, mt in results:
                    
                    # 🌟 核心智能分流
                    target_status = "DOWNLOADED" if is_trusted else "PENDING"
                    target_folder = "official" if is_trusted else "quarantine"
                    
                    source_code = "REEL" if getattr(post, 'typename', '') == "GraphVideo" else "POST"
                    asset_data = {
                        "source_type_id": self.db.status_manager.get_id("SOURCE_TYPE", source_code),
                        "download_status_id": self.db.status_manager.get_id("DOWNLOAD_STATUS", target_status),
                        "original_username": account,
                        "original_shortcode": post.shortcode,
                        "source_is_verified": int(profile.is_verified)
                    }
                    
                    # 👇 同樣使用動態的 target_folder
                    self._save_and_dispatch_memory(system_name, fn, main_bytes, ai_bytes, asset_data, target_folder)

                time.sleep(random.uniform(2, 5))

            if new_checkpoint and (scan_completed or checkpoint is None):
                self.checkpoints.save_checkpoint(system_name, account, new_checkpoint)
            else:
                logger.warning("⚠️ 掃描未完整銜接，不更新檢查點。")

        except Exception as e:
            logger.error(f"❌ 帳號處理異常: {e}")

    def _save_and_dispatch_memory(self, system_name: str, fn: str, main_bytes: bytes, ai_bytes: bytes, asset_data: dict, target_folder: str = "official"):
        # 1. 依據傳入的目標資料夾動態組裝 S3 路徑
        s3_key_main = f"{system_name}/{target_folder}/{fn}"
        s3_uri_main = f"s3://{self.router.bucket_name}/{s3_key_main}"
        
        try:
            # ==========================================
            # ☁️ 第一階段：雲端無狀態直傳 (Cloud-Native Upload)
            # ==========================================
            content_type = "video/mp4" if fn.endswith(".mp4") else "image/jpeg"
            self.router.s3_client.put_object(
                Bucket=self.router.bucket_name, 
                Key=s3_key_main,
                Body=main_bytes, 
                ContentType=content_type
            )

            if ai_bytes and ai_bytes != main_bytes:
                ai_fn = fn.replace(".mp4", ".jpg")
                s3_key_ai = f"{system_name}/{target_folder}/{ai_fn}"
                self.router.s3_client.put_object(
                    Bucket=self.router.bucket_name, 
                    Key=s3_key_ai,
                    Body=ai_bytes, 
                    ContentType="image/jpeg"
                )

            asset_data["file_name"] = fn
            asset_data["file_path"] = s3_key_main
            asset_data["system_name"] = system_name
            
            # 呼叫我們上一階段重寫的防呆寫入方法
            media_id = self.db.insert_media_asset(asset_data)
            
            if media_id:
                # 🛡️ 防呆：絕對只允許狀態為 DOWNLOADED 的直通車檔案進入 AI 佇列
                downloaded_id = self.db.status_manager.get_id("DOWNLOAD_STATUS", "DOWNLOADED")
                if asset_data.get("download_status_id") == downloaded_id:
                    
                    payload = {"media_id": media_id}
                    
                    # 🌟 修正：使用自建的 RedisPublisher 進行安全推播
                    self.redis.push_task(payload)
                    
                    logger.info(f"🚀 [放行] 成功推送任務至 S2 AI 大腦: {fn} (MediaID: {media_id})") 
                else:
                    logger.info(f"👀 [隔離] 檔案已存入緩衝區，等待前端人工審核: {fn}")
            else:
                logger.warning(f"♻️ DB 已有紀錄或寫入失敗，略過處理: {fn}")

        except Exception as e:
            logger.error(f"⚠️ 雲端直傳與派發發生異常: {e}")

    def watchdog_orphaned_downloads(self, profiles):
        """[雲端改寫版] 掃描 S3 孤兒檔案並重新推進 Redis"""
        logger.info("🐕 啟動 Watchdog：掃描 S3 下載區...")
        current_time = time.time()
        recovered_count = 0
        
        queue_length = self.redis.get_queue_length()
        buffer_seconds = 10 if queue_length == 0 else 1800
        
        for system_name in profiles.keys():
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
        """隨機測試模式"""
        logger.info("🧪 啟動 S1 Producer [隨機單次測試模式 - 雲原生版]...")
        profiles = self.db.get_dynamic_profiles()
        whitelist = self.db.get_dynamic_whitelist()
        
        all_targets = [(sys_name, acc) for sys_name, accs in profiles.items() for acc in accs]
        if not all_targets: return

        target_sys, target_acc = random.choice(all_targets)
        self.process_one_account(target_sys, target_acc, whitelist)

    def run(self):
        """正式輪詢模式"""
        logger.info("🔥 啟動 S1 Producer [正式運行模式 - 雲原生無狀態版]...")
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
    # 預設執行正式模式。如果要單次測試，可改為 producer.run_random_test()
    producer.run_random_test()