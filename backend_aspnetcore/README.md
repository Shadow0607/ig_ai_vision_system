# IG AI Vision System - .NET Backend

本子系統為整個 IG AI 視覺系統的後端控制中心 (API Gateway)，負責資料庫操作、狀態快取、認證與授權，並提供即時通訊服務。

## 技術棧
- **架構**：ASP.NET Core (C#)
- **資料庫 ORM**：Entity Framework Core (MySQL)
- **快取與訊息佇列**：StackExchange.Redis
- **物件儲存**：MinIO SDK
- **即時推播**：SignalR
- **驗證**：RSA JWT (非對稱加密)

## 核心功能與服務
1. **RESTful API**：提供前端進行系統狀態監控、人工審查照片、重啟或強制觸發 Worker 等操作。
2. **即時推播 (MonitorHub)**：前端透過 SignalR 連線，後端會將 Redis 的佇列狀態與 Worker 處理進度即時推播到前端。
3. **背景服務 (Hosted Services)**：
   - `RedisMonitorService`：監控 Redis 佇列堆積狀況。
   - `OrphanFileSweeperService`：自動清理 MinIO 中遺失關聯的孤兒檔案。
   - `BatchReclassifyWorkerService`：批次重新分類任務管理。
4. **JWT 認證**：使用 `.env` 內的 `JWT_PUBLIC_KEY` 進行非對稱金鑰解碼與驗證請求。

## 環境變數配置
系統依賴根目錄的 `.env` 檔案（使用 `DotNetEnv` 動態地毯式往上搜尋載入），主要包含：
- `DB_HOST`, `DB_USER`, `DB_PASSWORD` 等資料庫連線資訊。
- `REDIS_HOST`, `REDIS_PORT`。
- `S3_ENDPOINT_URL`, `S3_ACCESS_KEY`, `S3_SECRET_KEY` 等 MinIO 資訊。
- `JWT_PUBLIC_KEY` 驗證金鑰。

## 啟動方式
```bash
cd IgAiBackend
dotnet run
```
啟動後會自動開啟 Swagger API 文件頁面 (預設 `http://localhost:5000/swagger`)。
