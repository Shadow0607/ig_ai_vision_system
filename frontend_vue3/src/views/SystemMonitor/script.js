/* src/views/SystemMonitor/script.js */
import { ref, computed, onMounted, onUnmounted } from 'vue';
import api from '../../api_clients/api';
import * as signalR from '@microsoft/signalr'; 

export default {
  name: 'SystemMonitor',
  setup() {
    const alerts = ref([]);
    const statistics = ref({ successCount: 0, skipCount: 0 });
    const lastUpdateTime = ref('-');
    let hubConnection = null; 

    const formatTime = () => {
      const now = new Date();
      return now.toLocaleTimeString('zh-TW', { hour12: false });
    };

    // 1. 初次載入：抓取資料庫目前的數據
    const fetchMonitorData = async () => {
      try {
        lastUpdateTime.value = formatTime();
        const [alertRes, statsRes] = await Promise.all([
          api.getSystemAlerts(),
          api.getAiStatistics()
        ]);
        alerts.value = alertRes.data || [];
        statistics.value = statsRes.data || { successCount: 0, skipCount: 0 };
      } catch (error) {
        console.error("初始監控資料獲取失敗", error);
      }
    };

    const successRatio = computed(() => {
      const total = statistics.value.successCount + statistics.value.skipCount;
      if (total === 0) return "0.00";
      return ((statistics.value.successCount / total) * 100).toFixed(1);
    });

    // 2. 🌟 建立 SignalR 連線與事件監聽
    const startSignalR = async () => {
      hubConnection = new signalR.HubConnectionBuilder()
        .withUrl("http://localhost:5000/hubs/monitor") // 這裡對應後端的路由
        .withAutomaticReconnect() // 自動斷線重連機制
        .build();

      // 👂 監聽後端傳來的「數據更新」事件
      hubConnection.on("UpdateStatistics", (dataJson) => {
        const data = JSON.parse(dataJson);
        statistics.value.successCount = data.successCount;
        statistics.value.skipCount = data.skipCount;
        lastUpdateTime.value = formatTime();
        console.log("⚡ [SignalR] 統計數據已即時更新");
      });

      // 👂 監聽後端傳來的「新告警」事件
      hubConnection.on("NewAlert", (alertJson) => {
        const newAlert = JSON.parse(alertJson);
        alerts.value.unshift(newAlert); // 將新告警塞到最前面
        if (alerts.value.length > 5) alerts.value.pop(); // 保持最多 5 筆
        lastUpdateTime.value = formatTime();
        console.log("🚨 [SignalR] 收到新系統告警");
      });

      try {
        await hubConnection.start();
        console.log("✅ SignalR 連線成功！即時監控已啟動。");
      } catch (err) {
        console.error("❌ SignalR 連線失敗: ", err);
      }
    };

    // 🌟 修正：直接大膽呼叫，不需要再判斷 localStorage 了！
    // (攔截器跟 Router 已經幫我們把關好權限了)
    onMounted(() => {
      fetchMonitorData(); 
      startSignalR();     
    });

    onUnmounted(() => {
      // 離開頁面時切斷連線，節省資源
      if (hubConnection) {
        hubConnection.stop();
      }
    });

    return {
      alerts,
      statistics,
      successRatio,
      lastUpdateTime
    };
  }
};