import os
import time
import shutil
import logging
import requests
from glob import glob
from platform import system
from sqlite3 import connect, OperationalError
from pathlib import Path
import instaloader
import json
from io import BytesIO

logger = logging.getLogger(__name__)

class IGClient:
    def __init__(self, base_storage_path=None):
        # 🌟 雲原生化：不再依賴實體路徑，傳入 None 也可以
        self.base_storage_path = base_storage_path
        
        # 雖然我們不再落地，但仍需初始化 Instaloader
        self.L = instaloader.Instaloader(
            dirname_pattern='dummy_dir',   # 不會用到，給個佔位符
            download_video_thumbnails=True,
            save_metadata=False,           
            download_comments=False,       
            download_geotags=False,        
            post_metadata_txt_pattern='',  
            storyitem_metadata_txt_pattern='' 
        )
        self.L.download_captions = False

    def get_cookiefile(self) -> str:
        """從 Firefox 取得 cookies.sqlite 檔案路徑"""
        default_cookiefile = {
            "Windows": "~/AppData/Roaming/Mozilla/Firefox/Profiles/*/cookies.sqlite",
            "Darwin": "~/Library/Application Support/Firefox/Profiles/*/cookies.sqlite",
        }.get(system(), "~/.mozilla/firefox/*/cookies.sqlite")
        
        cookiefiles = glob(os.path.expanduser(default_cookiefile))
        if not cookiefiles:
            raise SystemExit("No Firefox cookies.sqlite file found.")
            
        cookiefiles.sort(key=os.path.getmtime, reverse=True)
        return cookiefiles[0]

    def get_session_from_firefox(self) -> str:
        """從 Firefox 讀取 cookie 並建立 session"""
        cookiefile = self.get_cookiefile()
        logger.info(f"Using cookies from {cookiefile}.")
        
        temp_cookie_path = "temp_cookies.sqlite"
        shutil.copy2(cookiefile, temp_cookie_path) 
        
        conn = connect(temp_cookie_path) 
        try:
            try:
                cookie_data = conn.execute("SELECT name, value FROM moz_cookies WHERE baseDomain='instagram.com'")
            except OperationalError:
                cookie_data = conn.execute("SELECT name, value FROM moz_cookies WHERE host LIKE '%instagram.com'")
            
            instaloader_for_session = instaloader.Instaloader(max_connection_attempts=1)
            instaloader_for_session.context._session.cookies.update(cookie_data)
            
            try:
                username = instaloader_for_session.test_login()
            except TypeError as e:
                if "'NoneType' object is not subscriptable" in str(e):
                    raise Exception("Instagram 拒絕了您的 Cookie！👉 解決方法：請打開 Firefox，進入 instagram.com，登出後再重新登入一次！")
                raise e
            
            if not username:
                raise SystemExit("Not logged in. Are you logged in successfully in Firefox?")
                
            logger.info(f"Imported session cookie for {username}.")
            instaloader_for_session.context.username = username
            instaloader_for_session.save_session_to_file()
            
            return username
        finally:
            conn.close()
            if os.path.exists(temp_cookie_path):
                try: os.remove(temp_cookie_path)
                except: pass

    def initialize_login(self) -> bool:
        """執行登入與掛載 Session 權限"""
        try:
            imported_username = self.get_session_from_firefox()
            self.L.load_session_from_file(imported_username)
            logger.info(f"✅ S1 爬蟲已成功掛載 {imported_username} 的權限")
            return True
        except Exception as e:
            logger.error(f"❌ 登入初始化失敗: {e}")
            return False

    def safe_generator(self, generator):
        """處理 API 限流與 Soft Ban 的安全產生器"""
        iterator = iter(generator)
        while True:
            try:
                yield next(iterator)
            except StopIteration: 
                break
            except Exception as e:
                if "Too many queries" in str(e) or "wait" in str(e):
                    logger.warning("⏳ 讀取列表被限流，冷卻 60 秒...")
                    time.sleep(60)
                    continue 
                elif "feedback_required" in str(e):
                    logger.error("🛑 帳號需驗證 (Soft Ban)")
                    break
                else: 
                    break

    def is_repost(self, post, is_story=False, target_username=None, system_name=None, current_whitelist=None) -> bool:
        """檢查內容是否為轉發 (包含動態 DB 白名單驗證)"""
        try:
            node = post._node
            if is_story:
                if 'story_feed_media' in node: return True
                if 'reshared_story_media_author' in node: return True
                if 'attached_media' in node and len(node._node['attached_media']) > 0: return True
            else:
                if target_username and post.owner_username != target_username:
                    allowed_collabs = (current_whitelist or {}).get(system_name, [])
                    if post.owner_username in allowed_collabs:
                        logger.info(f"🤝 觸發 DB 白名單特例: 允許下載 {post.owner_username} 發布的協作貼文")
                        return False 
                    return True 
        except Exception as e:
            pass
        return False

    def get_profile(self, account: str):
        """取得目標帳號的 Profile 物件"""
        return instaloader.Profile.from_username(self.L.context, account)

    def _fetch_bytes_from_url(self, url: str) -> bytes:
        if not url: return None
        try:
            headers = {
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Referer": "https://www.instagram.com/",
                "Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8"
            }
            
            resp_fallback = requests.get(url, headers=headers, timeout=15)
            if resp_fallback.status_code == 200:
                return resp_fallback.content
                
            resp = self.L.context._session.get(url, headers=headers, timeout=15)
            if resp.status_code == 200:
                return resp.content
                
            logger.error(f"❌ 雙重下載皆失敗！最終狀態碼: {resp.status_code}，網址: {url[:60]}...")
            return None
            
        except Exception as e:
            logger.error(f"❌ 記憶體下載發生異常: {e}")
            return None

    def download_media_as_bytes(self, post, system_name: str, is_story: bool = False):
        """
        🌟 無狀態下載版：直接解析 IG CDN 網址並轉成 bytes 傳出，不落地。
        完美支援單圖、單影片、旋轉木馬多圖多影 (Carousel)。
        """
        try:
            results = []
            base_filename = self.L.format_filename(post)

            if is_story:
                v_bytes = self._fetch_bytes_from_url(post.video_url) if post.is_video else None
                i_bytes = self._fetch_bytes_from_url(post.url)
                
                results.append({
                    "filename": base_filename,
                    "video_bytes": v_bytes,
                    "image_bytes": i_bytes,
                    "is_video": post.is_video,
                    "typename": "Story"
                })
            else:
                if getattr(post, 'typename', '') == 'GraphSidecar':
                    for idx, node in enumerate(post.get_sidecar_nodes(), start=1):
                        v_bytes = self._fetch_bytes_from_url(node.video_url) if node.is_video else None
                        i_bytes = self._fetch_bytes_from_url(node.display_url)
                        
                        results.append({
                            "filename": f"{base_filename}_{idx}",
                            "video_bytes": v_bytes,
                            "image_bytes": i_bytes,
                            "is_video": node.is_video,
                            "typename": "POST"
                        })
                else:
                    v_bytes = self._fetch_bytes_from_url(post.video_url) if post.is_video else None
                    i_bytes = self._fetch_bytes_from_url(post.url)
                    
                    results.append({
                        "filename": base_filename,
                        "video_bytes": v_bytes,
                        "image_bytes": i_bytes,
                        "is_video": post.is_video,
                        "typename": getattr(post, 'typename', 'POST')
                    })
                    
            return results

        except Exception as e:
            if "Too many queries" in str(e): raise e 
            logger.error(f"❌ 解析媒體網址異常: {e}")
            return []

    def download_media_to_memory(self, post) -> bytes:
        """🌟 輔助方法：單一媒體下載至記憶體 (供 process_post 使用)"""
        url = getattr(post, 'video_url', None) if getattr(post, 'is_video', False) else getattr(post, 'url', None)
        return self._fetch_bytes_from_url(url)

    def _determine_source_type(self, post, db_repo) -> int:
        """🌟 輔助方法：動態取得來源類型的 ID，徹底拔除 29, 30, 31 硬編碼"""
        typename = getattr(post, 'typename', '')
        if typename == 'GraphVideo':
            return db_repo.status_manager.get_id("SOURCE_TYPE", "REEL")
        elif typename in ['GraphImage', 'GraphSidecar']:
            return db_repo.status_manager.get_id("SOURCE_TYPE", "POST")
        else:
            return db_repo.status_manager.get_id("SOURCE_TYPE", "STORY")

    def process_post(self, post, target_person_id, db_repo, minio_client):
        """處理單一 IG 貼文/限動的四軌動態路由"""
        original_username = post.owner_profile.username
        shortcode = post.shortcode
        
        # 🌟 1. 預先從 Redis 狀態字典中動態抓取所有需要的 ID
        downloaded_id = db_repo.status_manager.get_id("DOWNLOAD_STATUS", "DOWNLOADED")
        pending_id = db_repo.status_manager.get_id("DOWNLOAD_STATUS", "PENDING")
        story_type_id = db_repo.status_manager.get_id("SOURCE_TYPE", "STORY")
        
        # 🌟 2. 動態判定來源類型 (POST, REEL, STORY)
        source_type_id = self._determine_source_type(post, db_repo)

        # 🛑 防呆防線：全局查重 (避免病毒式轉發導致重複下載)
        if db_repo.is_shortcode_exists(shortcode):
            logger.debug(f"⏭️ 略過：Shortcode [{shortcode}] 已存在系統中。")
            return

        # 🔍 決策節點 1：信任度驗證 (Identity & Dynamic Trust)
        account_type_id = db_repo.get_account_type(original_username)
        is_ig_verified = post.owner_profile.is_verified # Instaloader 擷取藍勾勾狀態
        
        # 🌟 修正 Bug：[6-9] 在 Python 會變成 [-3]，改回正確的白名單陣列 [1, 2, 3] (本帳, 官方, 小帳)
        is_trusted_express = (account_type_id in [1, 2, 3]) or is_ig_verified

        if is_trusted_express:
            # ==========================================
            # 🚀 軌道 A1/A2：官方與藍勾勾直通車 (Express Lane)
            # ==========================================
            logger.info(f"🚀 [直通車放行] 信任來源 [{shortcode}] (來源: {original_username}, 藍勾勾: {is_ig_verified})")
            
            # 1. 直接下載實體檔案 (存入 Memory)
            media_bytes = self.download_media_to_memory(post)
            file_name = f"official_{shortcode}.mp4" if post.is_video else f"official_{shortcode}.jpg"
            
            # 2. 存入 MinIO 正式儲存區
            minio_path = minio_client.upload_bytes("ig-ai-assets", f"official/{file_name}", media_bytes)
            
            # 3. 寫入 DB，狀態為 DOWNLOADED id，並註記藍勾勾狀態
            media_id = db_repo.insert_media_asset({
                "person_id": target_person_id,
                "file_path": minio_path,
                "source_type_id": source_type_id,
                "download_status_id": downloaded_id, # 🌟 動態 ID: DOWNLOADED
                "original_username": original_username,
                "original_shortcode": shortcode,
                "source_is_verified": 1 if is_ig_verified else 0
            })
            
            # 4. 立即推入 Redis AI 處理佇列，啟動 S2 分析
            if hasattr(self, 'redis_client'):
                queue_payload = json.dumps({"media_id": media_id})
                self.redis_client.lpush("ig_processing_queue", queue_payload)

        else:
            # ==========================================
            # 🛡️ 決策節點 2：普通帳號的時效性分流 (Hybrid Storage)
            # ==========================================
            logger.info(f"👀 [進入緩衝區] 普通轉發 [{shortcode}] (來源: {original_username})")
            
            if source_type_id == story_type_id: # 🌟 動態 ID 取代原本的 31
                # 🚨 軌道 C: STORY 限動 (有 24H 時效性，先下載實體檔並隔離)
                media_bytes = self.download_media_to_memory(post)
                file_name = f"quarantine_{shortcode}.mp4" if post.is_video else f"quarantine_{shortcode}.jpg"
                
                # 存入 MinIO 隔離區 (Quarantine)
                minio_path = minio_client.upload_bytes("ig-ai-assets", f"quarantine/{file_name}", media_bytes)
                
                db_repo.insert_media_asset({
                    "person_id": target_person_id,
                    "file_path": minio_path,
                    "source_type_id": story_type_id,  # 🌟 動態 ID
                    "download_status_id": pending_id, # 🌟 動態 ID: PENDING
                    "original_username": original_username,
                    "original_shortcode": shortcode,
                    "source_is_verified": 0
                })
                # 🛑 阻斷：絕對不推入 ig_processing_queue
                
            else:
                # 🛡️ 軌道 B: POST/REEL (永久性質，Metadata-First 僅存網址)
                db_repo.insert_media_asset({
                    "person_id": target_person_id,
                    "file_path": post.url, # 絕對不要下載實體，暫存 IG CDN 的縮圖網址
                    "source_type_id": source_type_id,
                    "download_status_id": pending_id, # 🌟 動態 ID: PENDING
                    "original_username": original_username,
                    "original_shortcode": shortcode,
                    "source_is_verified": 0
                })
                # 🛑 阻斷：等待審核員核准後才觸發真實下載