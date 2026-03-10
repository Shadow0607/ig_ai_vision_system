import os
import boto3
import pymysql
import logging
from io import BytesIO
from botocore.config import Config
from shared.sys_status_manager import SysStatusManager
logger = logging.getLogger(__name__)

class FileAndDBRouter:
    def __init__(self, db_host, db_port, db_user, db_password, db_name):
        logger.info("☁️ 初始化 S3 與資料庫路由系統 (Cloud-Native)")
        
        # ================= 1. 資料庫設定 =================
        self.db_config = {
            'host': db_host,
            'port': int(db_port),
            'user': db_user,
            'password': db_password,
            'database': db_name,
            'charset': 'utf8mb4',
            'cursorclass': pymysql.cursors.DictCursor
        }

        # ================= 2. S3 / MinIO 連線設定 =================
        s3_endpoint = os.getenv('S3_ENDPOINT', 'localhost:9000')
        # 確保 endpoint 包含 http:// (boto3 必填)
        endpoint_url = f"http://{s3_endpoint}" if not s3_endpoint.startswith('http') else s3_endpoint
        
        # 🌟 將原本的 S3_BUCKET_NAME 統一改為 MINIO_BUCKET_NAME
        self.bucket_name = os.getenv('MINIO_BUCKET_NAME', 'ig-ai-assets')
        
        # 建立 boto3 連線實體
        self.s3_client = boto3.client(
            's3',
            endpoint_url=endpoint_url,
            aws_access_key_id=os.getenv('S3_ACCESS_KEY', 'admin'),
            aws_secret_access_key=os.getenv('S3_SECRET_KEY', 'ai_vision_secret_s3'),
            config=Config(signature_version='s3v4'),
            region_name='us-east-1' # MinIO 預設相容 region
        )
        self.status_manager = SysStatusManager()

    # ================= ☁️ S3 雲端檔案操作 (取代原本的本機寫入) =================

    def upload_file(self, local_path: str, object_key: str) -> bool:
        """[給 S1 爬蟲使用] 將暫存檔上傳至 S3 雲端"""
        try:
            self.s3_client.upload_file(local_path, self.bucket_name, object_key)
            return True
        except Exception as e:
            logger.error(f"❌ S3 上傳失敗 ({object_key}): {e}")
            return False

    def get_object_stream(self, object_key: str) -> BytesIO:
        """[給 S2 AI 使用] 🌟 檔案不落地！直接從 S3 下載成記憶體串流 (BytesIO)"""
        try:
            response = self.s3_client.get_object(Bucket=self.bucket_name, Key=object_key)
            return BytesIO(response['Body'].read())
        except Exception as e:
            logger.error(f"❌ S3 讀取失敗 ({object_key}): {e}")
            return None

    def download_file(self, object_key: str, local_path: str) -> bool:
        """[給影片或模型使用] 將大檔從 S3 下載到本機暫存"""
        try:
            self.s3_client.download_file(self.bucket_name, object_key, local_path)
            return True
        except Exception as e:
            return False

    def move_file_safe(self, src_key: str, dest_folder: str) -> str:
        """[權責分離版] 僅在 S3 內進行複製，不執行物理刪除"""
        if not src_key: return None
        try:
            filename = os.path.basename(src_key)
            system_name = src_key.split('/')[0] if '/' in src_key else "unknown"
            new_key = f"{system_name}/{dest_folder}/{filename}"

            if src_key == new_key:
                return new_key

            # 🌟 1. 僅執行複製到新位置 (例如：從 downloads 到 GARBAGE)
            copy_source = {'Bucket': self.bucket_name, 'Key': src_key}
            self.s3_client.copy_object(Bucket=self.bucket_name, CopySource=copy_source, Key=new_key)
            
            # 🌟 2. 移除 self.s3_client.delete_object(Bucket=self.bucket_name, Key=src_key)
            # 原有的刪除動作現在交給 C# OrphanFileSweeperService 統一處理
            logger.info(f"🚚 檔案已複製至標記目錄: {new_key} (等待 C# 清理舊檔)")
            
            return new_key
        except Exception as e:
            logger.error(f"❌ S3 複製失敗 ({src_key} -> {dest_folder}): {e}")
            return None

    # ================= 🗄️ 資料庫操作 (邏輯不變，僅路徑概念轉為 Object Key) =================

    def _get_connection(self):
        return pymysql.connect(**self.db_config)

    def get_person_threshold(self, system_name: str) -> float:
        """從資料庫取得該人物的 AI 辨識門檻"""
        conn = self._get_connection()
        try:
            with conn.cursor() as cursor:
                cursor.execute("SELECT threshold FROM target_persons WHERE system_name = %s", (system_name,))
                res = cursor.fetchone()
                return res['threshold'] if res and res['threshold'] else 0.45
        except Exception as e:
            return 0.45 
        finally:
            conn.close()

    def update_db_log(self, media_id: int, status_code: str, face_detected: bool = False, score: float = 0.0):
        if not media_id: return
        
        # 轉換為狀態 ID
        status_id = self.status_manager.get_id("AI_RECOGNITION", status_code)
        if not status_id:
            logger.error(f"❌ 查無狀態碼 {status_code}，略過 DB 更新")
            return

        conn = self._get_connection()
        try:
            with conn.cursor() as cursor:
                # 🌟 欄位從 recognition_status 換成 status_id
                sql = """
                    INSERT INTO ai_analysis_logs 
                    (media_id, face_detected, status_id, confidence_score) 
                    VALUES (%s, %s, %s, %s)
                """
                cursor.execute(sql, (media_id, 1 if face_detected else 0, status_id, score))
                conn.commit()
        except Exception as e: 
            logger.error(f"❌ DB 日誌更新失敗: {e}")
        finally: 
            conn.close()

    def update_media_asset_path(self, media_id: int, new_key: str):
        if not media_id or not new_key: return
        conn = self._get_connection()
        try:
            with conn.cursor() as cursor:
                # 🌟 寫入的是乾淨的 S3 Object Key (例: yoona__lim/pos/1.jpg)
                sql = "UPDATE media_assets SET file_path = %s WHERE id = %s"
                cursor.execute(sql, (str(new_key), media_id))
                conn.commit()
        except Exception as e: pass
        finally: conn.close()

    def get_real_db_path(self, media_id: int) -> str:
        """取得資料庫紀錄的 S3 Object Key"""
        conn = self._get_connection()
        try:
            with conn.cursor() as cursor:
                cursor.execute("SELECT file_path FROM media_assets WHERE id = %s", (media_id,))
                res = cursor.fetchone()
                return res['file_path'] if res else None
        finally: conn.close()

    def get_media_id_by_filename(self, filename: str, system_name: str) -> int:
        conn = self._get_connection()
        try:
            with conn.cursor() as cursor:
                sql = "SELECT id FROM media_assets WHERE file_name = %s AND system_name = %s"
                cursor.execute(sql, (filename, system_name))
                res = cursor.fetchone()
                return res['id'] if res else None
        finally: conn.close()