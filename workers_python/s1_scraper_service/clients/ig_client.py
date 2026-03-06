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

    # 🌟 新增輔助方法：透過 requests 在記憶體下載二進位數據
    # 🌟 新增輔助方法：透過 requests 在記憶體下載二進位數據 (升級防禦版)
    def _fetch_bytes_from_url(self, url: str) -> bytes:
        if not url: return None
        try:
            # 1. 加入標準瀏覽器標頭，防止 IG CDN 403 阻擋
            headers = {
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Referer": "https://www.instagram.com/",
                "Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8"
            }
            
            # 策略 A: 先用 Instaloader 的登入 Session 讀取
            resp = self.L.context._session.get(url, headers=headers, timeout=15)
            if resp.status_code == 200:
                return resp.content
                
            # 策略 B: 如果 Session 被擋 (通常是 403)，改用裸 requests (CDN 網址通常可直接被公開讀取)
            logger.warning(f"⚠️ Session 讀取受阻 (代碼: {resp.status_code})，切換無狀態讀取...")
            resp_fallback = requests.get(url, headers=headers, timeout=15)
            if resp_fallback.status_code == 200:
                return resp_fallback.content
                
            # 如果兩次都失敗，大聲印出錯誤，不再默默死掉
            logger.error(f"❌ 雙重下載皆失敗！最終狀態碼: {resp_fallback.status_code}，網址: {url[:60]}...")
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
                # 限時動態
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
                # 一般貼文
                if getattr(post, 'typename', '') == 'GraphSidecar':
                    # 處理旋轉木馬 (多圖/多影片)
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
                    # 單圖或單影片
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