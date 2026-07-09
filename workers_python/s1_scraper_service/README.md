# IG AI Vision System - S1 Scraper Service (Producer)

本子系統為 Python 實作的爬蟲服務，扮演資料生產者 (Producer) 的角色，負責從 Instagram 等社群平台自動採集影像並分發任務。

## 技術棧
- **語言**：Python 3
- **核心套件**：Redis, Instaloader (或自建 IG Client), YouTube-DL (YT Client), OpenCV/FFmpeg (輕量處理)

## 核心邏輯 (`main_producer.py`)
1. **動態地圖讀取**：向 MySQL 索取 `active_map`，取得目前被標記為需要監控的 IG 帳號與 YT 頻道列表。
2. **抓取與去重**：
   - 抓取使用者的限時動態 (Stories) 與貼文 (Posts/Reels)。
   - 將影像轉換為二進位 (Bytes)，並使用 `HashHelper` 計算 **pHash** (感知雜湊) 與 **MD5** 雙重指紋。
   - 比對 MySQL，若雙指紋存在即丟棄，防止重複下載。
3. **上傳至 MinIO**：
   - 影片檔案若為靜態影片，會自動降級為圖片處理 (`MemoryMediaProcessor`)。
   - 將檔案直接 Stream 上傳到 MinIO 物件儲存中。
4. **派發任務**：
   - 若為信任的官方帳號，會將該筆資料的 `media_id` 打包推送到 Redis 佇列 `ig_processing_queue`。
   - 若為非信任帳號，則狀態設為待審查，直接丟入隔離區 (Quarantine)。
   
## 啟動方式
```bash
# 請確保環境變數 (.env) 與套件已正確配置
python main_producer.py
```
