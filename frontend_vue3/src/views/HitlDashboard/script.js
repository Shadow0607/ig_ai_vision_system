import { ref, computed, onMounted } from 'vue';
import api from '@/api_clients/api';
import { usePermissions } from '@/composables/usePermissions';

export default {
  name: 'HitlDashboard',
  setup() {
    // =====================================================
    // 全域與權限狀態
    // =====================================================
    const activeTab = ref('ai-review'); 
    const fullViewImage = ref(null);
    const apiBaseUrl = "http://localhost:5000"; 

    // 🌟 讀取路由守衛準備好的全域安全記憶體
    const { myPerms } = usePermissions('HitlDashboard');

    // 權限防呆判斷
    const canReview = computed(() => {
      return myPerms.value?.actions?.includes('APPROVE') || myPerms.value?.actions?.includes('UPDATE');
    });

    // =====================================================
    // 模塊 1：AI 特徵批次審核 (100% 保留您原本的邏輯)
    // =====================================================
    const pendingImages = ref([]);
    const selectedIds = ref([]);

    const fetchPendingImages = async () => {
      try {
        const response = await api.getHitlPendingImages();
        pendingImages.value = response.data || [];
        selectedIds.value = [];
      } catch (error) {
        console.error("無法取得 HITL 清單", error);
        if (error.response?.status !== 401) {
          alert("無法取得待審核名單，請檢查後端連線或權限狀態。");
        }
      }
    };

    const toggleSelection = (id) => {
      const idx = selectedIds.value.indexOf(id);
      if (idx === -1) selectedIds.value.push(id);
      else selectedIds.value.splice(idx, 1);
    };

    const selectAll = () => { selectedIds.value = pendingImages.value.map(img => img.id); };
    const clearSelection = () => { selectedIds.value = []; };

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
        alert(`❌ 排除失敗: ${error.response?.data?.message || error.message}`);
      }
    };

    const openFullView = (url) => { fullViewImage.value = url; };
    const closeFullView = () => { fullViewImage.value = null; };

    // =====================================================
    // 模塊 2：未知轉發與限動隔離審核 (新功能)
    // =====================================================
    const repostPendingList = ref([]);
    const isProcessing = ref(null);

    const loadRepostPending = async () => {
      try {
        // 🌟 修正點：改為呼叫剛剛在 api.js 中註冊的具名函數
        const res = await api.getPendingReposts();
        repostPendingList.value = res.data || [];
      } catch (error) {
        console.error('載入轉發清單失敗', error);
        if (error.response?.status !== 401) {
           alert("無法取得轉發審核名單，請檢查連線狀態。");
        }
      }
    };

    const decideRepost = async (mediaId, action) => {
      if (action === 'DELETE' && !confirm('確定要捨棄此檔案嗎？若為限動將會從 NAS 中永久刪除！')) return;
      
      isProcessing.value = mediaId;
      const originalList = [...repostPendingList.value];
      repostPendingList.value = repostPendingList.value.filter(x => x.id !== mediaId); // 樂觀更新
      
      try {
        // 🌟 修正點：改為呼叫 api.decideRepost，並傳入與 C# DTO 完全對齊的物件
        await api.decideRepost({ 
          mediaId: mediaId, 
          action: action 
        });
      } catch (error) {
        alert(`操作失敗: ${error.response?.data?.message || error.message}`);
        repostPendingList.value = originalList; // 失敗則 Rollback
      } finally {
        isProcessing.value = null;
      }
    };

    const formatDate = (dateString) => {
      const d = new Date(dateString);
      return `${d.getMonth() + 1}/${d.getDate()} ${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    };

    // =====================================================
    // 生命週期掛載：一次載入兩個模塊的資料
    // =====================================================
    onMounted(() => {
      fetchPendingImages();
      loadRepostPending();
    });

    // 🌟 核心關鍵：將所有 Template 需要用到的變數與方法，全部 return 出去
    return {
      activeTab,
      fullViewImage,
      apiBaseUrl,
      canReview,
      pendingImages,
      selectedIds,
      toggleSelection,
      selectAll,
      clearSelection,
      getScoreColor,
      submitBatchApproval,
      handleReject,
      openFullView,
      closeFullView,
      repostPendingList,
      isProcessing,
      decideRepost,
      formatDate
    };
  }
};
