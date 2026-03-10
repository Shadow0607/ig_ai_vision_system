import os
import sys
import time
import random
import logging
import shutil
from pathlib import Path
import redis
workers_dir = str(Path(__file__).resolve().parent.parent)
if workers_dir not in sys.path:
    sys.path.insert(0, workers_dir)
from shared.config_loader import setup_project_env
ROOT_DIR = setup_project_env()

from clients.ig_client import IGClient
from clients.yt_client import YTClient 
from clients.redis_publisher import RedisPublisher
from state_management.db_repository import DBRepository
from state_management.checkpoint_tracker import CheckpointTracker
from utils.media_ffmpeg import MemoryMediaProcessor 
from workers_python.s2_ai_consumer_service.storage.file_router import FileAndDBRouter

logging.basicConfig(level=logging.INFO, format='%(asctime)s - [S1-Cloud] - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class S1Producer:
    def __init__(self):
        # 初始化核心組件
        self.router = FileAndDBRouter(
            db_host=os.getenv('DB_HOST'),
            db_port=os.getenv('DB_PORT'),
            db_user=os.getenv('DB_USER'),
            db_password=os.getenv('DB_PASSWORD'),
            db_name=os.getenv('DB_NAME')
        )
        self.ig = IGClient(None) 
        self.yt = YTClient() 
        self.redis = RedisPublisher()
        self.db = DBRepository()
        
        self.raw_redis = redis.Redis(
            host=os.getenv('REDIS_HOST', 'localhost'),
            port=int(os.getenv('REDIS_PORT', 6379)),
            password=os.getenv('REDIS_PASSWORD'),
            decode_responses=True
        )
        self.checkpoints = CheckpointTracker(self.raw_redis)

        if not self.ig.initialize_login():
            logger.error("❌ Instagram 登入失敗，程式退出")
            sys.exit(1)

    def _save_and_dispatch_memory(self, system_name: str, fn: str, main_bytes: bytes, ai_bytes: bytes, asset_data: dict, target_folder: str):
        """核心資產分發：上傳 S3 並寫入 DB 與任務佇列"""
        s3_key_main = f"{system_name}/{target_folder}/{fn}"
        try:
            # 1. 上傳主檔案 (圖片或影片)
            content_type = "video/mp4" if fn.endswith(".mp4") else "image/jpeg"
            self.router.s3_client.put_object(
                Bucket=self.router.bucket_name, Key=s3_key_main, Body=main_bytes, ContentType=content_type
            )

            # 2. 如果是影片，額外上傳一張用於 AI 辨識的封面圖
            if ai_bytes and ai_bytes != main_bytes:
                ai_fn = fn.replace(".mp4", ".jpg")
                self.router.s3_client.put_object(
                    Bucket=self.router.bucket_name, Key=f"{system_name}/{target_folder}/{ai_fn}", Body=ai_bytes, ContentType="image/jpeg"
                )

            # 3. 完善 asset 資訊並寫入 DB
            asset_data.update({"file_name": fn, "file_path": s3_key_main, "system_name": system_name})
            media_id = self.db.insert_media_asset(asset_data)
            
            if media_id:
                # 若為 DOWNLOADED 狀態則直接推送到 S2 AI 消費端
                downloaded_id = self.db.status_manager.get_id("DOWNLOAD_STATUS", "DOWNLOADED")
                if asset_data.get("download_status_id") == downloaded_id:
                    self.redis.push_task({"media_id": media_id})
                    logger.info(f"🚀 [放行] 成功推送任務: {fn} (MediaID: {media_id})") 
                else:
                    logger.info(f"👀 [隔離] 已存入隔離區等待審核: {fn}")
        except Exception as e:
            logger.error(f"⚠️ 資產派發異常: {e}")

    def process_media_pipeline(self, post, system_name: str, is_story: bool = False):
        """處理媒體下載與靜態影片降級邏輯"""
        media_list = self.ig.download_media_as_bytes(post, system_name, is_story)
        if not media_list: return []
        valid_media = []
        for media_info in media_list:
            filename, v_bytes, i_bytes = media_info["filename"], media_info.get("video_bytes"), media_info.get("image_bytes")
            media_type = "VIDEO" if media_info.get("is_video") else "IMAGE"
            main_bytes, ai_bytes, db_fn = i_bytes, i_bytes, f"{filename}.jpg"

            if media_type == "VIDEO" and v_bytes:
                if MemoryMediaProcessor.is_video_static_bytes(v_bytes):
                    media_type, db_fn = "IMAGE", f"{filename}.jpg"
                else:
                    main_bytes, ai_bytes, db_fn = v_bytes, i_bytes, f"{filename}.mp4"

            if main_bytes: valid_media.append((main_bytes, ai_bytes, db_fn, media_type))
        return valid_media

    def process_one_ig_account(self, account: str, meta: dict, full_ig_map: dict):
        """[地圖驅動版] 處理單一 IG 帳號"""
        system_name, type_id = meta['system_name'], meta['type_id']
        logger.info(f"\n🔍 掃描 IG: {account} (@{system_name}) [等級: {type_id}]")

        try:
            profile = self.ig.get_profile(account)
            # 依據帳號類型 (1:本帳, 2:官方, 3:小帳) 判定是否信任 
            is_trusted = profile.is_verified or (type_id in [1, 2, 3])
            
            # --- Stories ---
            if profile.has_public_story:
                for story in self.ig.L.get_stories(userids=[profile.userid]):
                    for item in story.get_items():
                        is_repost = self.ig.is_repost(item, is_story=True, system_name=system_name)
                        results = self.process_media_pipeline(item, system_name, is_story=True)
                        for m_bytes, a_bytes, fn, mt in results:
                            target_status = "DOWNLOADED" if (is_trusted and not is_repost) else "PENDING"
                            target_folder = "official" if (is_trusted and not is_repost) else "quarantine"
                            asset_data = {
                                "source_type_id": self.db.status_manager.get_id("SOURCE_TYPE", "STORY"),
                                "download_status_id": self.db.status_manager.get_id("DOWNLOAD_STATUS", target_status),
                                "original_username": account, "original_shortcode": getattr(item, 'shortcode', ''),
                                "ig_media_id": str(item.mediaid), "source_is_verified": int(profile.is_verified)
                            }
                            self._save_and_dispatch_memory(system_name, fn, m_bytes, a_bytes, asset_data, target_folder)

            # --- Posts ---
            checkpoint = self.checkpoints.get_checkpoint(system_name, account)
            new_cp, is_first, scan_done = None, True, False
            for post in self.ig.safe_generator(profile.get_posts()):
                if self.checkpoints.is_post_pinned_safe(post): continue
                if self.ig.is_repost(post, False, account, system_name, full_ig_map): continue

                if is_first: new_cp, is_first = post.shortcode, False
                if checkpoint and post.shortcode == checkpoint: 
                    scan_done = True
                    break
                
                results = self.process_media_pipeline(post, system_name)
                for m_bytes, a_bytes, fn, mt in results:
                    target_status, target_folder = ("DOWNLOADED", "official") if is_trusted else ("PENDING", "quarantine")
                    s_code = "REEL" if getattr(post, 'typename', '') == "GraphVideo" else "POST"
                    asset_data = {
                        "source_type_id": self.db.status_manager.get_id("SOURCE_TYPE", s_code),
                        "download_status_id": self.db.status_manager.get_id("DOWNLOAD_STATUS", target_status),
                        "original_username": account, "original_shortcode": post.shortcode,
                        "ig_media_id": str(post.mediaid), "source_is_verified": int(profile.is_verified)
                    }
                    self._save_and_dispatch_memory(system_name, fn, m_bytes, a_bytes, asset_data, target_folder)
                time.sleep(random.uniform(2, 4))
            
            if new_cp and (scan_done or checkpoint is None):
                self.checkpoints.save_checkpoint(system_name, account, new_cp)
        except Exception as e:
            logger.error(f"❌ IG 處理異常 ({account}): {e}")

    def process_youtube_channel(self, system_name: str, channel_handle: str):
        """[地圖驅動版] 處理 YouTube 頻道"""
        logger.info(f"📺 掃描 YT: {system_name} (@{channel_handle})")
        urls = [f"https://www.youtube.com/{channel_handle}/shorts", f"https://www.youtube.com/{channel_handle}/videos"]
        
        for url in urls:
            try:
                videos = self.yt.get_channel_videos(url)
                for v in videos:
                    if self.db.is_shortcode_exists(v['id']): continue
                    
                    v_path, t_path, temp_d = self.yt.download_video_to_temp(v['url'])
                    if not v_path: continue
                    
                    with open(v_path, 'rb') as f: m_bytes = f.read()
                    a_bytes = None
                    if t_path and os.path.exists(t_path):
                        with open(t_path, 'rb') as f: a_bytes = f.read()
                    
                    s_code = "YOUTUBE_SHORT" if v['is_shorts'] else "YOUTUBE_VIDEO"
                    asset_data = {
                        "source_type_id": self.db.status_manager.get_id("SOURCE_TYPE", s_code),
                        "download_status_id": self.db.status_manager.get_id("DOWNLOAD_STATUS", "DOWNLOADED"),
                        "original_username": system_name, "original_shortcode": v['id'],
                        "ig_media_id": v['id'], "source_is_verified": 1
                    }
                    self._save_and_dispatch_memory(system_name, f"yt_{v['id']}.mp4", m_bytes, a_bytes, asset_data, "official")
                    shutil.rmtree(temp_d, ignore_errors=True)
                    time.sleep(random.uniform(3, 6))
            except Exception as e:
                logger.error(f"❌ YT 處理異常 ({url}): {e}")
    def test_platform_only(self, platform_code: str, target_account: str = None):
        """
        [測試專用] 針對單一平台進行掃描測試
        :param platform_code: 平台代碼 (如 'ig', 'yt', 'tiktok')
        :param target_account: (選填) 指定測試的帳號 ID，若不填則測試該平台所有監控帳號
        """
        logger.info(f"🧪 啟動單一平台測試模式: [{platform_code}]")
        
        # 🌟 1. 取得全平台地圖
        full_map = self.db.get_full_active_map()
        
        if platform_code not in full_map:
            logger.error(f"❌ 找不到平台: {platform_code}，請檢查資料庫 platforms 表格")
            return

        accounts = full_map[platform_code]
        
        # 🌟 2. 篩選測試目標
        test_targets = []
        if target_account:
            if target_account in accounts:
                test_targets.append((target_account, accounts[target_account]))
            else:
                logger.error(f"❌ 平台 {platform_code} 中找不到帳號: {target_account}")
                return
        else:
            # 若沒指定帳號，則抓取該平台所有標記為監控中的帳號
            test_targets = [(acc, meta) for acc, meta in accounts.items() if meta['is_monitored']]

        if not test_targets:
            logger.warning(f"⚠️ 平台 {platform_code} 沒有可測試的監控目標")
            return

        # 🌟 3. 執行對應引擎
        for acc, meta in test_targets:
            logger.info(f"▶️ 正在測試: {acc} (System: {meta['system_name']})")
            
            if platform_code == 'ig':
                # IG 需要完整 map 來作為轉發判定的白名單
                self.process_one_ig_account(acc, meta, accounts)
                
            elif platform_code == 'yt':
                # YouTube 引擎直接處理
                self.process_youtube_channel(meta['system_name'], acc)
            
            # 如果未來有 tiktok 等平台，只需在此擴充邏輯
            
        logger.info(f"🏁 平台 [{platform_code}] 測試完成")

    def run(self):
        """[雙引擎並行] 主輪詢邏輯"""
        logger.info("🔥 S1 Producer 啟動 [字典高效版]...")
        while True:
            # 🌟 每次開始掃描前，只查一次 DB 獲取所有平台的活躍帳號字典
            full_map = self.db.get_full_active_map()
            
            # 遍歷資料庫中動態定義的所有平台
            for platform, accounts in full_map.items():
                if platform == 'ig':
                    for acc, meta in accounts.items():
                        if meta['is_monitored']:
                            self.process_one_ig_account(acc, meta, accounts)
                
                elif platform == 'yt':
                    for handle, meta in accounts.items():
                        if meta['is_monitored']:
                            self.process_youtube_channel(meta['system_name'], handle)
            
            logger.info("💤 本輪結束，休息 15 分鐘...")
            time.sleep(900)

if __name__ == "__main__":
    #S1Producer().run()
    producer = S1Producer()
    # 只跑 IG 平台，不跑 YouTube
    producer.test_platform_only('yt')