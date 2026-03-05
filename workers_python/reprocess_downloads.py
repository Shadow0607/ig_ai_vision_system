import os
import sys
import json
import redis
from pathlib import Path
from dotenv import load_dotenv

# 設定
QUEUE_NAME = "ig_processing_queue"
script_dir = Path(__file__).resolve().parent
env_path = script_dir.parent / '.env'
load_dotenv(dotenv_path=env_path)

def main():
    if len(sys.argv) < 2:
        print("❌ 請輸入人物 SystemName")
        print("範例: python reprocess_downloads.py neck_na_a")
        sys.exit(1)

    system_name = sys.argv[1]
    
    # 自動抓取 storage_volumes 路徑
    base_storage = script_dir.parent / "storage_volumes"
    downloads_dir = base_storage / system_name / "downloads"

    if not downloads_dir.exists():
        print(f"❌ 找不到目錄: {downloads_dir}")
        sys.exit(1)

    # 連線 Redis
    try:
        pool = redis.ConnectionPool(
            host=os.getenv('REDIS_HOST'),
            port=int(os.getenv('REDIS_PORT')),
            password=os.getenv('REDIS_PASSWORD'),
            decode_responses=True
        )
        r = redis.Redis(connection_pool=pool)
        r.ping()
    except Exception as e:
        print(f"❌ Redis 連線失敗: {e}")
        sys.exit(1)

    # 掃描檔案
    print(f"📂 正在掃描: {downloads_dir}")
    files = [f for f in os.listdir(downloads_dir) if f.lower().endswith(('.jpg', '.jpeg', '.png', '.mp4'))]

    if not files:
        print("✅ Downloads 資料夾是空的，沒有積壓檔案。")
        return

    print(f"🚀 發現 {len(files)} 個檔案，正在推送到處理佇列...")

    count = 0
    for f in files:
        file_path = str(downloads_dir / f)
        
        # 建立任務 JSON
        # 注意：這裡不填 task_id，讓 S2 透過檔名去 DB 反查
        payload = {
            "profile": system_name,
            "file_path": file_path,
            "timestamp": 0,
            "reprocess": True
        }
        
        r.lpush(QUEUE_NAME, json.dumps(payload))
        count += 1

    print(f"✅ 成功推送 {count} 筆任務！")
    print("👉 請確保 's2_to_s4_ai_consumer.py' 正在執行中，它會開始消化這些檔案。")

if __name__ == "__main__":
    main()