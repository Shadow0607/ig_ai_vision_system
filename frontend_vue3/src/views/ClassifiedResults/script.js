import { ref, onMounted, computed } from 'vue';
import api from '@/api_clients/api.js';
// 🌟 1. 引入剛剛拆分好的安全組件
import SafeMediaDisplay from '../SafeMediaDisplay/index.vue';

export default {
  // 🌟 2. 註冊組件
  components: {
    SafeMediaDisplay
  },
  setup() {
    const mediaList = ref([]);
    const loading = ref(false);
    const currentStatus = ref('OUTPUT');
    
    // 🌟 3. 改為存儲整個物件而非單一 URL
    const fullViewItem = ref(null);
    
    const selectedIds = ref([]);
    const personsList = ref([]);
    const selectedAccount = ref(''); 
    const currentPage = ref(1);
    const pageSize = ref(20);
    const totalPages = ref(1);
    const totalItems = ref(0);

    const tabs = [
      { label: '🌟 本人(Match)', value: 'OUTPUT' },
      { label: '❓ 待覆核(PENDING)', value: 'PENDING' },
      { label: '🗑️ 排除與垃圾(Rejected)', value: 'REJECTED' }, 
      { label: '👤 無人臉(NoFace)', value: 'NOFACE' }
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
        const res = await api.getClassifiedMedia(currentStatus.value, selectedAccount.value, currentPage.value, pageSize.value);
        mediaList.value = res.data.items || [];
        totalPages.value = res.data.totalPages || 1;
        totalItems.value = res.data.totalItems || 0;
      } catch (err) { console.error(err); } 
      finally { loading.value = false; }
    };

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

    const changePage = (page) => {
      if (page >= 1 && page <= totalPages.value) {
        currentPage.value = page;
        selectedIds.value = []; 
        fetchData();
      }
    };

    const onPageSizeChange = () => {
      currentPage.value = 1;
      fetchData();
    };

    const getScoreClass = (score) => {
      if (score >= 0.8) return 'high-score';
      if (score >= 0.5) return 'mid-score';
      return 'low-score';
    };

    const formatDate = (d) => new Date(d).toLocaleString();
    
    // 🌟 4. 開啟放大檢視：直接存入 item 物件
    const openFullView = (item) => {
      fullViewItem.value = item;
    };
    const closeFullView = () => {
      fullViewItem.value = null;
    };

    const toggleSelection = (id) => {
      const idx = selectedIds.value.indexOf(id);
      if (idx > -1) selectedIds.value.splice(idx, 1);
      else selectedIds.value.push(id);
    };

    const isAllSelected = computed(() => {
      return mediaList.value.length > 0 && selectedIds.value.length === mediaList.value.length;
    });

    const toggleSelectAll = () => {
      if (isAllSelected.value) {
        selectedIds.value = [];
      } else {
        selectedIds.value = mediaList.value.map(item => item.id);
      }
    };

    const reclassify = async (item, newStatusId) => {
      const actionName = newStatusId === 4 ? '恢復為本人' : '移至排除區';
      if (!confirm(`確定要將此影像 ${actionName} 嗎？`)) return;

      try {
        await api.reclassifyMedia({ logId: item.id, newStatusId: newStatusId });
        await fetchData();
        selectedIds.value = selectedIds.value.filter(id => id !== item.id);
      } catch (err) { alert("操作失敗：" + (err.response?.data?.message || err.message)); }
    };

    const batchReclassify = async (newStatusId) => {
      const actionName = newStatusId === 4 ? '恢復為本人' : '移至排除區';
      if (!confirm(`確定要將這 ${selectedIds.value.length} 筆影像 ${actionName} 嗎？`)) return;

      try {
        await api.batchReclassifyMedia({ logIds: selectedIds.value, newStatusId: newStatusId });
        selectedIds.value = [];
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
      fullViewItem, openFullView, closeFullView, reclassify,
      selectedIds, toggleSelection, batchReclassify,
      personsList, selectedAccount, onAccountChange,
      currentPage, pageSize, totalPages, totalItems, changePage, onPageSizeChange,
      isAllSelected, toggleSelectAll
    };
  }
}