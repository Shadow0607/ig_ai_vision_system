# IG AI Vision System

這是一個分散式、微服務架構的社群媒體爬蟲與 AI 視覺辨識系統，主要用於自動化採集 Instagram/YouTube 等社群平台的媒體資源，並透過深度學習模型進行人臉辨識與歸檔。

## 系統架構

本專案採用微服務設計，包含五個主要區塊：基礎設施、Python 爬蟲端 (Producer)、Python AI 分析端 (Consumer)、.NET 後端 API 以及 Vue 3 前端介面。

### 1. 基礎設施 (Infrastructure)
底層服務透過 `docker-compose.yml` 進行統一管理：
- **MySQL (8.0)**：關聯式資料庫，儲存系統狀態、媒體資產詮釋資料 (Media Assets)、使用者帳號等地圖與日誌。
- **Redis (7)**：作為訊息佇列 (Message Queue) 與快取層。Python 爬蟲與 AI 消費者透過它傳遞任務；.NET 後端使用它進行狀態快取。
- **MinIO**：S3 相容的物件儲存庫，統一存放爬蟲抓取回來的原始圖片/影片，以及 AI 處理後的資源 (`ig-ai-assets`)。
- **Qdrant**：向量資料庫，儲存與比對從圖片中萃取出來的人臉特徵向量 (Face Embeddings)。
- **Ethereum Node (Geth)**：私有區塊鏈節點，提供資料不可竄改之上鏈備查機制。

### 2. Python Worker：S1 爬蟲服務 (Producer)
- **位置**：`workers_python/s1_scraper_service/`
- **功能**：負責向外採集資料的「生產者」。
- **邏輯**：
  1. 根據資料庫動態載入監控地圖 (如 IG、YouTube 帳號)。
  2. 下載貼文與限時動態，並透過 pHash 與 MD5 雙指紋去重，過濾重複資料。
  3. 將下載的實體檔案上傳至 **MinIO** 儲存。
  4. 將待處理的媒體任務 (`media_id`) 發佈到 **Redis Queue** 交由 AI 端處理；若來源不明確則移入隔離區等待人工審查。

### 3. Python Worker：S2 AI 辨識服務 (Consumer)
- **位置**：`workers_python/s2_ai_consumer_service/`
- **功能**：負責處理佇列任務並執行人臉辨識的「消費者」。
- **邏輯**：
  1. 透過多執行緒監聽 **Redis Queue**，取得處理任務。
  2. 從 **MinIO** 取回影像，並使用 RetinaFace / MTCNN 提取人臉特徵。
  3. 將特徵送往 **Qdrant** 進行向量比對 (DecisionEngine)。
  4. 依據 AI 判定分數，自動將照片/影片分類為 `OUTPUT` (本人)、`PENDING` (待定) 或 `REJECTED` (非本人)，並更新 MinIO 檔案位置及資料庫狀態。

### 4. .NET 後端 API (Backend)
- **位置**：`backend_aspnetcore/IgAiBackend/`
- **技術棧**：ASP.NET Core, Entity Framework Core, SignalR, JWT
- **功能**：系統的中樞管理與前端 API 閘道器。
  1. 提供帶有 RSA JWT 認證的 RESTful API。
  2. 操作 MySQL 與 MinIO 提供資料與媒體服務。
  3. 使用 SignalR (`MonitorHub`) 即時向前端推播系統狀態與 Worker 進度。
  4. 執行背景服務 (Hosted Services) 如佇列監控 (`RedisMonitorService`)。

### 5. 前端介面 (Frontend)
- **位置**：`frontend_vue3/`
- **技術棧**：Vue 3 + Vite, Axios, SignalR
- **功能**：系統的使用者監控儀表板，即時顯示爬蟲與 AI 分析進度、資料庫報表，並提供人工介入審查的介面。

---

## 核心資料流 (Data Flow)

1. **採集**：`S1 Producer` 定期掃描目標帳號 ➜ 下載圖影 ➜ 存入 `MinIO` ➜ 記錄至 `MySQL` ➜ 推送任務至 `Redis Queue`。
2. **AI 分析**：`S2 Consumer` 收到 `Redis` 任務 ➜ 取出 `MinIO` 影像 ➜ 提取特徵去 `Qdrant` 向量比對 ➜ 更新分類與狀態。
3. **管理與呈現**：`.NET Backend` 監聽資料變化並將結果透過 `SignalR` 推播 ➜ 使用者在 `Vue 3` 前端儀表板即時查看或進行後續操作。

## 執行與部署
1. 確保已安裝 Docker 與 Docker Compose。
2. 在根目錄執行 `docker-compose up -d` 啟動基礎設施。
3. 分別啟動 `.NET Backend`、`Vue 3 Frontend` 與兩個 `Python Workers` (S1 & S2)。
