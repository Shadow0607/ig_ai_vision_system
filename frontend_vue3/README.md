# IG AI Vision System - Vue 3 Frontend

本子系統為系統的網頁控制面板 (Dashboard)，供管理員監控爬蟲進度、AI 辨識結果，以及進行人工介入審查 (Human-in-the-loop)。

## 技術棧
- **框架**：Vue 3 (Composition API)
- **建置工具**：Vite
- **路由**：Vue Router
- **HTTP 請求**：Axios
- **即時通訊**：@microsoft/signalr

## 核心功能
1. **系統監控儀表板**：
   - 即時顯示目前的爬蟲佇列與 AI 辨識佇列堆積數量。
   - 透過 SignalR 即時接收後端 (.NET) 傳來的系統與 Worker 狀態。
2. **人工審核介面**：
   - 顯示被 AI 判定為 `PENDING` (待定) 或被隔離區 (`quarantine`) 攔截的影像。
   - 提供按鈕允許操作員手動標示「是本人」或「不是本人」。
3. **動態載入圖片**：
   - 由於圖片存放於 MinIO (S3)，前端透過後端 API 或 MinIO 的公開網址安全地取回影像並展示。

## 啟動方式
開發環境啟動：
```bash
npm install
npm run dev
```
啟動後預設會運行在 `http://localhost:5173`。

編譯發布：
```bash
npm run build
```
