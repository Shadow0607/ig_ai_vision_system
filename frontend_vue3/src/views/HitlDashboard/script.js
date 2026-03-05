/* src/views/HitlDashboard/script.js */
import { ref, computed, onMounted } from 'vue';
import api from '../../api_clients/api';

export default {
  name: 'HitlDashboard',
  setup() {
    const apiBaseUrl = "http://localhost:5000";
    const pendingImages = ref([]);
    const selectedIds = ref([]);

    // 全螢幕預覽變數
    const fullViewImage = ref(null);

    // 🌟 修正 1：直接讀取路由守衛準備好的全域安全記憶體
    const myPerms = computed(() => {
      const permissions = window.__USER_PERMISSIONS__ || [];
      const p = permissions.find(item => item.routeName === 'HitlDashboard');
      return p || { canView: true, canCreate: false, canUpdate: false, canDelete: false };
    });

    const fetchPendingImages = async () => {
      try {
        const response = await api.getHitlPendingImages();
        pendingImages.value = response.data || [];
        selectedIds.value = [];
      } catch (error) {
        console.error("無法取得 HITL 清單", error);
        // 如果是 401 錯誤，代表 Cookie 過期，不用跳 Alert，Router 會處理
        if (error.response?.status !== 401) {
            alert("無法取得待審核名單，請檢查後端連線或權限狀態。");
        }
      }
    };

    const toggleSelection = (id) => {
      const idx = selectedIds.value.indexOf(id);
      if (idx === -1) {
        selectedIds.value.push(id);
      } else {
        selectedIds.value.splice(idx, 1);
      }
    };

    const selectAll = () => {
      selectedIds.value = pendingImages.value.map(img => img.id);
    };

    const clearSelection = () => {
      selectedIds.value = [];
    };

    const getScoreColor = (score) => {
      if (score >= 0.82) return '#4caf50';
      if (score >= 0.78) return '#ffb300';
      return '#f44336';
    };

    const submitBatchApproval = async () => {
      if (selectedIds.value.length === 0) return;
      if (!confirm(`確定要將這 ${selectedIds.value.length} 張照片標記為本人，並疊加至 AI 特徵庫嗎？`)) return;

      try {
        await api.approveHitlBatch(selectedIds.value);
        alert(`✅ 成功通過 ${selectedIds.value.length} 筆特徵！AI 正在後台進行疊加學習。`);
        await fetchPendingImages();
      } catch (error) {
        console.error("批次確認失敗", error);
        alert(`❌ 審核失敗: ${error.response?.data?.message || error.message}`);
      }
    };

    const handleReject = async (id) => {
      try {
        await api.rejectHitlImage(id);
        pendingImages.value = pendingImages.value.filter(img => img.id !== id);
        const selIdx = selectedIds.value.indexOf(id);
        if (selIdx !== -1) selectedIds.value.splice(selIdx, 1);
      } catch (error) {
        console.error("排除失敗", error);
        const errMsg = error.response?.data?.message || error.message;
        alert(`❌ 排除失敗: ${errMsg}`);
      }
    };

    const openFullView = (url) => { fullViewImage.value = url; };
    const closeFullView = () => { fullViewImage.value = null; };

    // 🌟 修正 2：拔除 localStorage 判斷，直接大膽呼叫 API！
    // (因為 axios 現在設定了 withCredentials: true，它會自動把 Cookie 送給後端)
    onMounted(() => {
      fetchPendingImages();
    });

    return {
      apiBaseUrl,
      pendingImages,
      selectedIds,
      fullViewImage,
      myPerms, 
      toggleSelection, selectAll, clearSelection, getScoreColor,
      submitBatchApproval, handleReject, openFullView, closeFullView
    };
  }
};