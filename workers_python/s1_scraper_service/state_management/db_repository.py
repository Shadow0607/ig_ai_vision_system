import os
import time
import logging
import requests
from mysql.connector import pooling
from shared.sys_status_manager import SysStatusManager
logger = logging.getLogger(__name__)

class DBRepository:
    def __init__(self):
        self.pool = self._create_db_pool()
        self.status_manager = SysStatusManager() # 確保此 Manager 有從 Redis 載入 sys_statuses
        
        # 🌟 修正：從環境變數動態讀取，預設給定開發環境
        base_api_url = os.getenv('API_BASE_URL', 'http://localhost:5000')
        self.api_config_url = f"{base_api_url}/api/Config/profiles/python-worker"
        self.api_whitelist_url = f"{base_api_url}/api/Config/whitelists/python-worker"

    def _create_db_pool(self):
        db_config = {
            'host': os.getenv('DB_HOST'),
            'port': int(os.getenv('DB_PORT')),
            'user': os.getenv('DB_USER'),
            'password': os.getenv('DB_PASSWORD'),
            'database': os.getenv('DB_NAME'),
            'charset': 'utf8mb4',
            'pool_name': 'ig_scraper_pool',
            'pool_size': 5,
            'pool_reset_session': True
        }
        return pooling.MySQLConnectionPool(**db_config)

    def _get_connection(self):
        for _ in range(3):
            try:
                conn = self.pool.get_connection()
                if conn.is_connected(): return conn
            except Exception as e:
                logger.warning(f"⚠️ 取得 DB 連線失敗，重試中... {e}")
                time.sleep(1)
        return None

    def get_dynamic_profiles(self) -> dict:
        try:
            res = requests.get(self.api_config_url, timeout=5)
            if res.status_code == 200: return res.json()
        except: pass
        return {}
    
    def get_dynamic_whitelist(self) -> dict:
        try:
            res = requests.get(self.api_whitelist_url, timeout=5)
            if res.status_code == 200: return res.json()
        except: pass
        return {}

    def insert_media_asset(self, asset_data: dict) -> int:
        """
        寫入媒體資產，並具備智慧型防呆推斷機制，確保不違反 MySQL NOT NULL 約束。
        """
        file_path = asset_data.get("file_path", "")

        # 🛡️ 防呆 1：自動解析檔名 (file_name)
        file_name = asset_data.get("file_name")
        if not file_name and file_path:
            file_name = file_path.split("/")[-1]

        # 🛡️ 防呆 2：自動判斷媒體類型 (media_type_id)
        # 依據 sys_statuses 定義: 34=IMAGE, 35=VIDEO
        media_type_id = asset_data.get("media_type_id")
        if not media_type_id:
            type_code = "VIDEO" if file_path.endswith(".mp4") else "IMAGE"
            media_type_id = self.status_manager.get_id("MEDIA_TYPE", type_code)

        # 🛡️ 防呆 3：自動反查人物系統代號 (system_name)
        system_name = asset_data.get("system_name")
        person_id = asset_data.get("person_id")
        if not system_name and person_id:
            system_name = self._get_system_name_by_person_id(person_id)

        # 明確且安全的參數化 SQL 語句
        query = """
            INSERT INTO media_assets (
                person_id, system_name, file_name, file_path,
                media_type_id, source_type_id, download_status_id,
                original_username, original_shortcode, source_is_verified
            ) VALUES (
                %s, %s, %s, %s, %s, %s, %s, %s, %s, %s
            )
        """
        
        # 嚴格的 Tuple 賦值，防止 SQL Injection
        values = (
            person_id,
            system_name or "unknown",
            file_name or "unknown",
            file_path,
            media_type_id,
            asset_data.get("source_type_id"),
            asset_data.get("download_status_id"),
            asset_data.get("original_username"),
            asset_data.get("original_shortcode"),
            asset_data.get("source_is_verified", 0)
        )

        conn = self.pool.get_connection()
        try:
            with conn.cursor() as cursor:
                cursor.execute(query, values)
                conn.commit()
                return cursor.lastrowid
        except Exception as e:
            logger.error(f"❌ 寫入 media_assets 失敗: {str(e)}\n資料: {values}")
            conn.rollback()
            return 0
        finally:
            conn.close()

    def _get_system_name_by_person_id(self, person_id: int) -> str:
        """從 target_persons 反查 system_name (用於防呆補全)"""
        query = "SELECT system_name FROM target_persons WHERE id = %s LIMIT 1"
        conn = self.pool.get_connection()
        try:
            with conn.cursor() as cursor:
                cursor.execute(query, (person_id,))
                result = cursor.fetchone()
                return result if result else "unknown"
        finally:
            conn.close()
    def is_shortcode_exists(self, shortcode: str) -> bool:
        """[防線一] 檢查是否已存在相同的轉發貼文/影片，防止重複下載"""
        if not shortcode:
            return False
        query = "SELECT id FROM media_assets WHERE original_shortcode = %s LIMIT 1"
        conn = self.pool.get_connection()
        try:
            with conn.cursor() as cursor:
                cursor.execute(query, (shortcode,))
                return cursor.fetchone() is not None
        finally:
            conn.close()

    def get_account_type(self, username: str) -> int:
        """查詢帳號白名單權限級別 (1:本帳, 2:官方, 3:小帳, 4:粉絲, 5:協作)"""
        if not username:
            return None
        query = """
            SELECT account_type_id FROM social_accounts 
            WHERE account_identifier = %s AND is_monitored = 1
            LIMIT 1
        """
        conn = self.pool.get_connection()
        try:
            with conn.cursor() as cursor:
                cursor.execute(query, (username,))
                result = cursor.fetchone()
                return result if result else None
        finally:
            conn.close()