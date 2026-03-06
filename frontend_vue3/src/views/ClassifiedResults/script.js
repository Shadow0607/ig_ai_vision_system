import { ref, onMounted, nextTick, computed } from 'vue';
import api from '@/api_clients/api.js';
import { usePermissions } from '@/composables/usePermissions';
export default {
  setup() {
    const mediaList = ref([]);
    const loading = ref(false);
    const currentStatus = ref('OUTPUT');
    const fullViewImage = ref(null);
    const selectedIds = ref([]);
    const videoPlayer = ref(null);

    const personsList = ref([]);
    const selectedAccount = ref(''); 

    // 🌟 新增：分頁狀態變數
    const currentPage = ref(1);
    const pageSize = ref(20);
    const totalPages = ref(1);
    const totalItems = ref(0);

    // script.js
    const tabs = [
      { label: '🌟 本人(Match)', value: 'OUTPUT' },
      { label: '❓ 待覆核(PENDING)', value: 'PENDING' },
      { label: '🗑️ 排除(Rejected)', value: 'REJECTED' }, // 這裡現在會包含 GARBAGE
      { label: '👤 無人臉(NoFace)', value: 'NOFACE' },
      { label: '⏩ 跳過(Skip)', value: 'SKIP' },     // 🌟 新增：看到被 AI 忽略的檔案
      { label: '♻️ 垃圾桶(Garbage)', value: 'GARBAGE' } // 🌟 新增：看到徹底被過濾的檔案
    ];
    const fetchPersons = async () => {
      try {
        const res = await api.getAllPersons();
        personsList.value = res.data;
      } catch (err) { console.error("無法取得人物清單:", err); }
    };

    const fetchData = async () => {
      loading.value = true;
      try {
        // 🌟 帶入分頁參數
        const res = await api.getClassifiedMedia(currentStatus.value, selectedAccount.value, currentPage.value, pageSize.value);
        // 🌟 對接後端回傳的新 JSON 結構 (res.data.items)
        mediaList.value = res.data.items || [];
        totalPages.value = res.data.totalPages || 1;
        totalItems.value = res.data.totalItems || 0;
      } catch (err) { console.error(err); } 
      finally { loading.value = false; }
    };

    // 🌟 改變狀態或帳號時，重置為第一頁
    const changeStatus = (status) => {
      currentStatus.value = status;
      selectedIds.value = [];
      currentPage.value = 1;
      fetchData();
    };

    const onAccountChange = () => {
      selectedIds.value = [];
      currentPage.value = 1;
      fetchData();
    };

    // 🌟 換頁與改變每頁筆數方法
    const changePage = (page) => {
      if (page >= 1 && page <= totalPages.value) {
        currentPage.value = page;
        selectedIds.value = []; // 換頁時清空打勾
        fetchData();
      }
    };

    const onPageSizeChange = () => {
      currentPage.value = 1; // 改變每頁數量時，回到第一頁
      fetchData();
    };

    const getScoreClass = (score) => {
      if (score >= 0.8) return 'high-score';
      if (score >= 0.5) return 'mid-score';
      return 'low-score';
    };

    const formatDate = (d) => new Date(d).toLocaleString();
    
    const openFullView = async (url) => {
      fullViewImage.value = url;
      if (url.toLowerCase().endsWith('.mp4')) {
        await nextTick();
        if (videoPlayer.value) {
          videoPlayer.value.play().catch(e => console.warn(e));
        }
      }
    };
    const closeFullView = () => fullViewImage.value = null;

    const toggleSelection = (id) => {
      const idx = selectedIds.value.indexOf(id);
      if (idx > -1) selectedIds.value.splice(idx, 1);
      else selectedIds.value.push(id);
    };
    const isAllSelected = computed(() => {
      return mediaList.value.length > 0 && selectedIds.value.length === mediaList.value.length;
    });

    // 🌟 3. 新增：切換全選 / 取消全選
    const toggleSelectAll = () => {
      if (isAllSelected.value) {
        selectedIds.value = []; // 如果已全選，則清空
      } else {
        selectedIds.value = mediaList.value.map(item => item.id); // 否則將本頁所有 ID 加入
      }
    };

    const reclassify = async (item, newStatusId) => {
      // 🌟 修正 1：改用數字來判斷對話框文字 (假設 2 是 OUTPUT)
      const actionName = newStatusId === 2 ? '恢復為本人' : '移至排除區';
      if (!confirm(`確定要將此影像 ${actionName} 嗎？`)) return;

      try {
        // 🌟 修正 2：明確將屬性命名為 newStatusId，精準對接後端的 DTO
        await api.reclassifyMedia({ logId: item.id, newStatusId: newStatusId });
        
        // 操作成功後，重新向後端拉取該頁資料，確保分頁總數正確
        await fetchData();
        selectedIds.value = selectedIds.value.filter(id => id !== item.id);
      } catch (err) { alert("操作失敗：" + (err.response?.data?.message || err.message)); }
    };

    const batchReclassify = async (newStatusId) => {
      // 🌟 修正 1：改用數字來判斷
      const actionName = newStatusId === 2 ? '恢復為本人' : '移至排除區';
      if (!confirm(`確定要將這 ${selectedIds.value.length} 筆影像 ${actionName} 嗎？`)) return;

      try {
        // 🌟 修正 2：明確將屬性命名為 newStatusId
        await api.batchReclassifyMedia({ logIds: selectedIds.value, newStatusId: newStatusId });
        selectedIds.value = [];
        // 操作成功後，重新向後端拉取該頁資料
        await fetchData();
      } catch (err) { alert("批量操作失敗：" + (err.response?.data?.message || err.message)); }
    };

    onMounted(async () => {
      await fetchPersons(); 
      await fetchData();   
    });

    return { 
      mediaList, loading, currentStatus, tabs, 
      changeStatus, getScoreClass, formatDate,
      fullViewImage, openFullView, closeFullView, reclassify,
      selectedIds, toggleSelection, batchReclassify, videoPlayer,
      personsList, selectedAccount, onAccountChange,
      currentPage, pageSize, totalPages, totalItems, changePage, onPageSizeChange,
      isAllSelected, toggleSelectAll // 👈 匯出分頁方法
    };
  }
}
