import os
import time
import shutil
import logging
from glob import glob
from platform import system
from sqlite3 import connect, OperationalError
from pathlib import Path
import instaloader

logger = logging.getLogger(__name__)

class IGClient:
    def __init__(self, base_storage_path: Path):
        self.base_storage_path = base_storage_path
        # 🟢 繼承原有的基礎設定，包含阻擋 .txt 產生的規則
        self.L = instaloader.Instaloader(
            dirname_pattern=str(self.base_storage_path / '{target}' / 'downloads'),
            download_video_thumbnails=True,
            save_metadata=False,           # 拒絕儲存 JSON metadata
            download_comments=False,       # 拒絕下載留言
            download_geotags=False,        # 拒絕下載地標
            post_metadata_txt_pattern='',  # 拒絕產生貼文內文 TXT
            storyitem_metadata_txt_pattern='' # 拒絕產生限動 TXT
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
            
        # 🟢 智慧防護：確保永遠抓到「最近剛使用過」的 Firefox 設定檔
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
                # 🟢 智慧防護：攔截 Instaloader 遇到過期 Cookie 時的崩潰
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

    def download_media(self, post, system_name: str, is_story: bool = False):
        """執行實際下載並回傳檔案資訊"""
        try:
            if is_story: 
                self.L.download_storyitem(post, target=system_name)
            else: 
                self.L.download_post(post, target=system_name)

            filename = self.L.format_filename(post)
            base_path = self.base_storage_path / system_name / "downloads" / filename
            
            return {
                "filename": filename,
                "video_file": f"{base_path}.mp4",
                "image_file": f"{base_path}.jpg",
                "is_video": post.is_video,
                "typename": getattr(post, 'typename', 'Story' if is_story else 'POST')
            }
        except Exception as e:
            if "Too many queries" in str(e): raise e 
            return None