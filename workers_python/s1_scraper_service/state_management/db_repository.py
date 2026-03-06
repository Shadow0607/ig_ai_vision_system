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

    def insert_media_record(self, system_name: str, ig_account_id: str, filename: str, s3_uri: str, media_type: str, source_type: str):
        conn = self._get_connection()
        if not conn: return None, False

        try:
            with conn.cursor(dictionary=True) as cursor:
                media_type_id = self.status_manager.get_id("MEDIA_TYPE", media_type) or \
                                self.status_manager.get_id("MEDIA_TYPE", "IMAGE")
                                
                source_type_id = self.status_manager.get_id("SOURCE_TYPE", source_type) or \
                                 self.status_manager.get_id("SOURCE_TYPE", "UNKNOWN")
                                 
                download_status_id = self.status_manager.get_id("DOWNLOAD_STATUS", "DOWNLOADED")

                # 🌟 資安防護：極端情況下若連預設 ID 都取不到 (如 Redis 徹底崩潰)
                # 必須攔截，防止 DB 拋出 IntegrityError
                if not download_status_id or not media_type_id or not source_type_id:
                    logger.error(f"🚨 [嚴重異常] 無法從 Redis 狀態字典獲取必要 ID。中止寫入以保護資料庫。")
                    return None, False

                check_sql = "SELECT id, download_status_id FROM media_assets WHERE file_name = %s AND system_name = %s"
                cursor.execute(check_sql, (filename, system_name))
                existing = cursor.fetchone()

                if existing:
                    # 若存在且已下載，直接回傳
                    if existing['download_status_id'] == download_status_id:
                        return existing['id'], True 
                    return None, False 

                sql_find = "SELECT p.id as pid, a.id as aid FROM target_persons p LEFT JOIN social_accounts a ON p.id = a.person_id AND a.account_identifier = %s WHERE p.system_name = %s"
                cursor.execute(sql_find, (ig_account_id, system_name))
                res = cursor.fetchone()
                if not res: 
                    logger.warning(f"⚠️ 找不到對應的目標人物或社群帳號: {ig_account_id}")
                    return None, False

                # 修改 SQL 語法，寫入 ID 外鍵
                sql_ins = """
                    INSERT INTO media_assets 
                    (person_id, system_name, account_id, file_name, file_path, media_type_id, source_type_id, download_status_id) 
                    VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
                """
                cursor.execute(sql_ins, (res['pid'], system_name, res['aid'], filename, s3_uri, media_type_id, source_type_id, download_status_id))
                conn.commit()
                return cursor.lastrowid, False
                
        except Exception as e:
            conn.rollback()
            logger.error(f"❌ DB Write Error in insert_media_record: {e}")
            return None, False
        finally:
            if conn:
                conn.close()