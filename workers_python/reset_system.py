import os
import redis
import pymysql
from pathlib import Path
from dotenv import load_dotenv

# ==========================================
# 1. 智慧型載入 .env
# ==========================================
current_dir = Path(__file__).resolve().parent
possible_paths = [
    current_dir / '.env',           # 1. 找當前目錄
    current_dir.parent / '.env',    # 2. 找上一層目錄
    Path.cwd() / '.env'             # 3. 找執行指令的目錄
]

env_loaded = False
for path in possible_paths:
    if path.exists():
        print(f"📄 找到設定檔: {path}")
        load_dotenv(dotenv_path=path)
        env_loaded = True
        break

if not env_loaded:
    print("❌ 錯誤: 找不到 .env 檔案！")
    print(f"請確認 .env 是否存在於以下路徑之一: {[str(p) for p in possible_paths]}")
    exit(1)

# 檢查關鍵變數是否讀取成功
redis_host = os.getenv('REDIS_HOST')
redis_port = os.getenv('REDIS_PORT')
db_host = os.getenv('DB_HOST')

if not redis_port or not db_host:
    print("❌ 錯誤: .env 檔案讀取到了，但內容似乎是空的或缺少 REDIS_PORT / DB_HOST。")
    print("請檢查 .env 內容。")
    exit(1)

print(f"✅ 環境變數載入成功 (Redis: {redis_host}:{redis_port})")

# ==========================================
# 2. 確認執行
# ==========================================
print("\n⚠️  警告：這將會清除所有資料庫紀錄與排程任務！")
confirm = input("❓ 確定要執行嗎？ (y/n): ")
if confirm.lower() != 'y':
    print("❌ 取消操作")
    exit()

# ==========================================
# 3. 清除 Redis
# ==========================================
try:
    print("\n🧹 正在清除 Redis...")
    r = redis.Redis(
        host=redis_host,
        port=int(redis_port),
        password=os.getenv('REDIS_PASSWORD'),
        decode_responses=True
    )
    r.flushdb()
    print("✅ Redis 已清空！")
except Exception as e:
    print(f"❌ Redis 清除失敗: {e}")

# ==========================================
# 4. 清除 MySQL
# ==========================================
try:
    print("\n🧹 正在清除 MySQL 資料庫...")
    conn = pymysql.connect(
        host=os.getenv('DB_HOST'),
        port=int(os.getenv('DB_PORT')),
        user=os.getenv('DB_USER'),
        password=os.getenv('DB_PASSWORD'),
        database=os.getenv('DB_NAME')
    )
    
    with conn.cursor() as cursor:
        cursor.execute("SET FOREIGN_KEY_CHECKS = 0")
        cursor.execute("TRUNCATE TABLE ai_analysis_logs")
        print("   - 已清空 ai_analysis_logs")
        cursor.execute("TRUNCATE TABLE media_assets")
        print("   - 已清空 media_assets")
        cursor.execute("SET FOREIGN_KEY_CHECKS = 1")
        
    conn.commit()
    conn.close()
    print("✅ MySQL 資料庫已重置！")

except Exception as e:
    print(f"❌ MySQL 清除失敗: {e}")

print("\n✨ 系統已重置完成！您可以重新啟動 S1 爬蟲了。")