import os
import time
import logging
import requests
from mysql.connector import pooling

logger = logging.getLogger(__name__)

class DBRepository:
    def __init__(self):
        self.pool = self._create_db_pool()
        self.api_config_url = "http://localhost:5000/api/Config/profiles/python-worker"
        self.api_whitelist_url = "http://localhost:5000/api/Config/whitelists/python-worker"

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
        """寫入資料庫，並回傳 (media_id, 是否為舊的未處理任務)"""
        conn = self._get_connection()
        if not conn:
            logger.error("❌ 無法取得 DB 連線，放棄寫入")
            return None, False

        try:
            with conn.cursor(dictionary=True) as cursor:
                # 1. 去重與狀態檢查
                check_sql = "SELECT id, download_status FROM media_assets WHERE file_name = %s AND system_name = %s"
                cursor.execute(check_sql, (filename, system_name))
                existing = cursor.fetchone()

                if existing:
                    # 殭屍任務復活判斷
                    if existing['download_status'] == 'DOWNLOADED':
                        return existing['id'], True # 是舊記錄且需重新推送
                    return None, False # 已處理過，直接跳過

                # 2. 寫入新紀錄
                sql_find = "SELECT p.id as pid, a.id as aid FROM target_persons p LEFT JOIN social_accounts a ON p.id = a.person_id AND a.account_identifier = %s WHERE p.system_name = %s"
                cursor.execute(sql_find, (ig_account_id, system_name))
                res = cursor.fetchone()
                if not res: return None, False

                sql_ins = """
                    INSERT INTO media_assets 
                    (person_id, system_name, account_id, file_name, file_path, media_type, source_type, download_status) 
                    VALUES (%s, %s, %s, %s, %s, %s, %s, 'DOWNLOADED')
                """
                cursor.execute(sql_ins, (res['pid'], system_name, res['aid'], filename, s3_uri, media_type, source_type))
                conn.commit()
                return cursor.lastrowid, False
                
        except Exception as e:
            logger.error(f"DB Write Error: {e}")
            return None, False
        finally:
            conn.close()