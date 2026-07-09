import os
import time
import logging
from mysql.connector import pooling
from shared.sys_status_manager import SysStatusManager

logger = logging.getLogger(__name__)

class DBRepository:
    def __init__(self):
        self.pool = self._create_db_pool()
        self.status_manager = SysStatusManager()
        
    def _create_db_pool(self):
        db_config = {
            'host': os.getenv('DB_HOST'),
            'port': int(os.getenv('DB_PORT', 3306)),
            'user': os.getenv('DB_USER'),
            'password': os.getenv('DB_PASSWORD'),
            'database': os.getenv('DB_NAME'),
            'charset': 'utf8mb4',
            'pool_name': 'ig_scraper_pool',
            'pool_size': 5,
            'pool_reset_session': True
        }
        return pooling.MySQLConnectionPool(**db_config)

    def _execute_query(self, query, params=None, fetch=True, dictionary=True):
        """統一的 SQL 執行工具"""
        conn = self.pool.get_connection()
        try:
            with conn.cursor(dictionary=dictionary) as cursor:
                cursor.execute(query, params or ())
                if fetch:
                    return cursor.fetchall()
                conn.commit()
                return cursor.lastrowid
        except Exception as e:
            logger.error(f"❌ SQL 執行異常: {e}")
            if not fetch: conn.rollback()
            return None
        finally:
            conn.close()

    # 🌟 動態生成平台字典的優化版本
    def get_full_active_map(self) -> dict:
        """
        1. 從 platforms 資料表動態抓取所有平台代碼 
        2. 抓取所有活躍帳號資料並自動分類
        """
        # --- 第一步：動態初始化字典 ---
        platforms_rows = self._execute_query("SELECT code FROM platforms")
        # 根據資料庫內容動態生成 {'yt': {}, 'ig': {}, ...} [cite: 23]
        data_map = {row['code']: {} for row in platforms_rows} if platforms_rows else {}

        # --- 第二步：抓取帳號細節並填入 ---
        query = """
            SELECT p.code AS platform, s.account_identifier, t.system_name, 
                   s.account_type_id, s.is_monitored
            FROM social_accounts s
            JOIN target_persons t ON s.person_id = t.id
            JOIN platforms p ON s.platform_id = p.id
            WHERE t.is_active = 1
        """
        results = self._execute_query(query)
        
        if results:
            for row in results:
                p_code = row['platform']
                # 確保平台代碼存在於 map 中（防呆）
                if p_code not in data_map:
                    data_map[p_code] = {}
                
                # 以帳號名稱作為 Key，儲存所有必要資訊
                data_map[p_code][row['account_identifier']] = {
                    'system_name': row['system_name'],     # 人物系統代號 [cite: 61]
                    'type_id': row['account_type_id'],     # 權限等級 (1-5) [cite: 36]
                    'is_monitored': row['is_monitored']    # 是否需掃描 [cite: 36]
                }
        return data_map
    # 在 DBRepository 類別中新增以下邏輯
    def is_any_hash_exists(self, p_hash: str, md5_hash: str) -> bool:
        """只要 pHash 或 MD5 其中一個在資料庫中已存在，即視為重複"""
        if not p_hash and not md5_hash: return False
        
        query = "SELECT id FROM media_assets WHERE image_hash = %s OR file_hash = %s LIMIT 1"
        conn = self.pool.get_connection()
        try:
            with conn.cursor() as cursor:
                cursor.execute(query, (p_hash, md5_hash))
                return cursor.fetchone() is not None
        finally:
            conn.close()

    # 記得修改 insert_media_asset 以接收並存入 file_hash 欄位

    def insert_media_asset(self, asset_data: dict) -> int:
        query = """
            INSERT INTO media_assets (
                person_id, system_name, file_name, file_path,
                media_type_id, source_type_id, download_status_id,
                original_username, original_shortcode, ig_media_id, 
                source_is_verified, image_hash, file_hash
            ) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
        """
        file_name = asset_data.get("file_name", "")
        m_type = "VIDEO" if file_name.lower().endswith(".mp4") else "IMAGE"
        media_type_id = self.status_manager.get_id("MEDIA_TYPE", m_type)

        values = (
            asset_data.get("person_id"),
            asset_data.get("system_name", "unknown"),
            file_name,
            asset_data.get("file_path"),
            media_type_id,
            asset_data.get("source_type_id"),
            asset_data.get("download_status_id"),
            asset_data.get("original_username"),
            asset_data.get("original_shortcode"),
            asset_data.get("ig_media_id"), 
            asset_data.get("source_is_verified", 0),
            asset_data.get("image_hash"), # 🌟 寫入 pHash
            asset_data.get("file_hash")   # 🌟 寫入 MD5
        )
        return self._execute_query(query, values, fetch=False)

    def is_shortcode_exists(self, shortcode: str) -> bool:
        """去重檢查：同時支援 IG 與 YouTube ID [cite: 16]"""
        if not shortcode: return False
        query = "SELECT id FROM media_assets WHERE original_shortcode = %s LIMIT 1"
        res = self._execute_query(query, (shortcode,))
        return len(res) > 0 if res else False