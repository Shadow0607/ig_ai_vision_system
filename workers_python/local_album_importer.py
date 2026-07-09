import os
import sys
import uuid
import json
import logging
from pathlib import Path

# 🌟 核心修正：確保 Python 能夠找到專案根目錄下的 utils 與 shared 模組
# 取得目前檔案的父目錄的父目錄（即專案根目錄 ig_ai_vision_system）
root_dir = str(Path(__file__).resolve().parent.parent)
if root_dir not in sys.path:
    sys.path.insert(0, root_dir)

# 現在可以安全地匯入專案內的模組了
from shared.config_loader import setup_project_env
setup_project_env() # 載入環境變數 (.env)

from utils.hash_helper import HashHelper
from state_management.db_repository import DBRepository
from workers_python.s2_ai_consumer_service.storage.file_router import FileAndDBRouter
from clients.redis_publisher import RedisPublisher

logger = logging.getLogger(__name__)

class LocalAlbumImporter:
    def __init__(self):
        self.db = DBRepository()
        self.router = FileAndDBRouter(
            db_host=os.getenv('DB_HOST'),
            db_port=os.getenv('DB_PORT'),
            db_user=os.getenv('DB_USER'),
            db_password=os.getenv('DB_PASSWORD'),
            db_name=os.getenv('DB_NAME')
        )
        # 🌟 取得 Redis 生產者實體
        self.publisher = RedisPublisher()
        
        # 取得本地導入專用的狀態 ID
        self.source_type_id = self.db.status_manager.get_id("SOURCE_TYPE", "LOCAL_IMPORT")
        self.downloaded_id = self.db.status_manager.get_id("DOWNLOAD_STATUS", "DOWNLOADED")

    def import_folder(self, system_name: str, local_path: str):
        """掃描本地路徑並導入至指定人物系統中"""
        logger.info(f"📁 開始導入本地相簿: {local_path} -> @{system_name}")
        
        # 遞迴搜尋圖片與影片
        extensions = ('.jpg', '.jpeg', '.png', '.webp', '.mp4')
        files = [f for f in Path(local_path).rglob('*') if f.suffix.lower() in extensions]
        
        success_count = 0
        skip_count = 0

        for file_path in files:
            try:
                with open(file_path, 'rb') as f:
                    file_bytes = f.read()

                # 🌟 1. 計算雙指紋
                p_hash, md5_hash = HashHelper.get_dual_fingerprints(file_bytes, file_path.name)
                
                # 🌟 2. 雙重查重門禁
                if self.db.is_any_hash_exists(p_hash, md5_hash):
                    logger.info(f"⏭️  [去重跳過] {file_path.name}")
                    skip_count += 1
                    continue

                unique_filename = f"{uuid.uuid4().hex[:8]}_{file_path.name}"
                target_key = f"{system_name}/official/{unique_filename}"
                
                # 🌟 3. 上傳至 MinIO 雲端儲存
                content_type = "video/mp4" if file_path.suffix.lower() == '.mp4' else "image/jpeg"
                self.router.s3_client.put_object(
                    Bucket=self.router.bucket_name,
                    Key=target_key,
                    Body=file_bytes,
                    ContentType=content_type
                )

                # 🌟 4. 寫入資料庫資產紀錄
                asset_data = {
                    "system_name": system_name,
                    "file_name": unique_filename,
                    "file_path": target_key,
                    "source_type_id": self.source_type_id,
                    "download_status_id": self.downloaded_id,
                    "image_hash": p_hash,
                    "file_hash": md5_hash
                }
                media_id = self.db.insert_media_asset(asset_data)
                
                # 🌟 5. 推送至 Redis 任務佇列，喚醒 S2 AI 消費者
                if media_id:
                    # 格式完全相容於 main_consumer.py 的 process_task
                    self.publisher.push_task({
                        "media_id": media_id,
                        "system_name": system_name,
                        "file_path": target_key,
                        "stage": "LOCAL_IMPORT"
                    })
                    success_count += 1
                    logger.info(f"✅ [導入成功] {unique_filename}")
            
            except Exception as e:
                logger.error(f"❌ 導入失敗 {file_path.name}: {e}")

        logger.info(f"🏁 導入結束。成功: {success_count}, 跳過重複: {skip_count}")
if __name__ == "__main__":
    import argparse

    # 1. 配置命令列參數解析器
    parser = argparse.ArgumentParser(description="🚀 IG AI Vision System - 本地相簿批量匯入工具")
    
    # 必要參數：系統代號與路徑
    parser.add_argument("system_name", help="目標人物的系統代號 (例如: yoona__lim)")
    parser.add_argument("local_path", help="本地相簿資料夾的完整路徑")

    # 2. 解析參數
    args = parser.parse_args()

    # 3. 執行前置檢查：確認路徑是否存在
    if not os.path.exists(args.local_path):
        print(f"\n❌ 錯誤：找不到指定的資料夾路徑 -> 「{args.local_path}」")
        print("💡 請檢查路徑是否正確，或是否包含空格（若有空格請用引號包覆路徑）。")
        sys.exit(1)

    # 4. 啟動匯入程序
    print("\n" + "="*50)
    print(f"🎬 準備啟動匯入程序")
    print(f"👤 目標人物: @{args.system_name}")
    print(f"📁 來源路徑: {args.local_path}")
    print("="*50 + "\n")

    try:
        importer = LocalAlbumImporter()
        importer.import_folder(args.system_name, args.local_path)
        
        print("\n" + "="*50)
        print(f"✨ 任務處理完成！")
        print(f"💡 請觀察 S2 AI Consumer 的日誌，確認 AI 是否已啟動辨識。")
        print("="*50)
        
    except KeyboardInterrupt:
        print("\n⚠️  使用者手動終止程序。")
    except Exception as e:
        logger.critical(f"💥 程式執行發生嚴重異常: {e}")
        sys.exit(1)