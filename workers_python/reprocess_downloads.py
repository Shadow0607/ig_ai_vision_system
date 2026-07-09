import os
import sys
import json
import redis
import mysql.connector
from shared.config_loader import setup_project_env

# 載入環境變數
setup_project_env()

QUEUE_NAME = "ig_processing_queue"

def main():
    if len(sys.argv) < 2:
        print("❌ 請輸入人物 SystemName")
        print("範例: python reprocess_downloads.py 54jojo1208")
        sys.exit(1)

    system_name = sys.argv[1]
    
    # 1. 連線 Redis
    try:
        pool = redis.ConnectionPool(
            host=os.getenv('REDIS_HOST', 'localhost'),
            port=int(os.getenv('REDIS_PORT', 6379)),
            password=os.getenv('REDIS_PASSWORD', ''),
            decode_responses=True
        )
        r = redis.Redis(connection_pool=pool)
        r.ping()
    except Exception as e:
        print(f"❌ Redis 連線失敗: {e}")
        sys.exit(1)

    # 2. 連線 MySQL
    try:
        db_conn = mysql.connector.connect(
            host=os.getenv('DB_HOST', 'localhost'),
            port=int(os.getenv('DB_PORT', 3306)),
            user=os.getenv('DB_USER', 'root'),
            password=os.getenv('DB_PASSWORD', ''),
            database=os.getenv('DB_NAME', 'ig_ai_system')
        )
        cursor = db_conn.cursor(dictionary=True)
    except Exception as e:
        print(f"❌ MySQL 連線失敗: {e}")
        sys.exit(1)

    print(f"🔍 正在資料庫中尋找 {system_name} 卡在 official 且未分析的檔案...")

    # 3. 撈取「在 official 裡面」且「沒有在 AiAnalysisLogs 裡面」的檔案
    query = """
        SELECT m.id, m.system_name, m.file_path, m.file_name 
        FROM media_assets m
        LEFT JOIN ai_analysis_logs a ON m.id = a.media_id
        WHERE m.system_name = %s 
          AND m.file_path LIKE '%/official/%'
          AND a.id IS NULL
    """
    cursor.execute(query, (system_name,))
    stuck_files = cursor.fetchall()

    if not stuck_files:
        print("✅ 太棒了！資料庫中沒有發現卡住的檔案。")
        cursor.close()
        db_conn.close()
        return

    print(f"🚀 發現 {len(stuck_files)} 個卡住的檔案，正在推送到 AI 處理佇列...")

    bucket_name = os.getenv('S3_BUCKET_NAME', 'ig-ai-assets')
    count = 0

    # 4. 推送任務至 Redis (採用符合 S2 架構的 Payload)
    for asset in stuck_files:
        # 確保路徑格式符合 S2 AI Worker 的預期 (依據您的系統，可能是 raw key 或 s3://)
        # 這裡我們傳遞 S5 也使用的標準 payload 格式
        payload = {
            "media_id": asset['id'],
            "system_name": asset['system_name'],
            # 如果 S2 預期有 s3:// 前綴就加上，若預期是純路徑則直接用 asset['file_path']
            "file_path": asset['file_path'], 
            "reprocess": True
        }
        
        r.lpush(QUEUE_NAME, json.dumps(payload))
        count += 1
        print(f"  👉 已推送: {asset['file_name']}")

    print(f"\n✅ 成功推送 {count} 筆任務！")
    print("👉 請檢查您的 Python AI Worker (s2_to_s4_ai_consumer.py) 的終端機，看看它是不是開始瘋狂消化了。")

    cursor.close()
    db_conn.close()

if __name__ == "__main__":
    main()