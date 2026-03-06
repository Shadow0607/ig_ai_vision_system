import { ref, computed, onMounted } from 'vue'; 
import api from '../../api_clients/api'; 
import { usePermissions } from '@/composables/usePermissions';
export default {
  name: 'ColdStartSetup',
  setup() {
    const loading = ref(false);
    const pendingList = ref([]);
    const currentIndex = ref(0);
    const selectedIds = ref([]);
    const fullViewImage = ref(null);
    const isUploading = ref(false);

    // 🌟 儲存帳號清單與當前選取的上傳目標
    const personsList = ref([]);
    const uploadTarget = ref('');

    // 計算權限旗標
    const { myPerms } = usePermissions('ColdStartSetup');

    const currentGroup = computed(() => {
      return pendingList.value[currentIndex.value] || null;
    });

    // 🌟 取得帳號清單給下拉選單使用
    const fetchPersons = async () => {
      try {
        const res = await api.getAllPersons();
        personsList.value = res.data;
      } catch(err) { 
        console.error("無法取得人物清單", err); 
      }
    };

    const fetchData = async () => {
      loading.value = true;
      try {
        const res = await api.getPendingColdStarts();
        pendingList.value = res.data;
        currentIndex.value = 0;
        selectedIds.value = [];
        if (pendingList.value.length > 0 && myPerms.value.canUpdate) {
          selectAll();
        }
      } catch (error) {
        console.error("無法取得冷啟動清單:", error);
      } finally {
        loading.value = false;
      }
    };

    // 🌟 上傳邏輯改為讀取 uploadTarget.value
    const handleFileUpload = async (event, isPositive) => {
      const files = event.target.files;
      if (!files.length || !uploadTarget.value) return;

      const actionName = isPositive ? '正樣本 (Match)' : '負樣本 (Garbage)';
      if (!confirm(`確定要為帳號「${uploadTarget.value}」上傳 ${files.length} 個檔案作為「${actionName}」嗎？\n上傳後會立即觸發 AI 模型重新訓練。`)) {
        event.target.value = ''; 
        return;
      }

      const formData = new FormData();
      for (let i = 0; i < files.length; i++) {
        formData.append('files', files[i]);
      }
      formData.append('isPositive', isPositive);

      isUploading.value = true;
      try {
        await api.uploadColdStartMedia(uploadTarget.value, formData);
        alert(`✅ 成功為 ${uploadTarget.value} 上傳 ${files.length} 筆${actionName}並觸發學習！`);
        await fetchData(); 
      } catch (err) {
        alert("上傳失敗：" + (err.response?.data?.message || err.message));
      } finally {
        isUploading.value = false;
        event.target.value = ''; 
      }
    };

    const switchPerson = (index) => {
      currentIndex.value = index;
      selectedIds.value = [];
      if (myPerms.value.canUpdate) selectAll();
    };

    const toggleSelection = (id) => {
      const idx = selectedIds.value.indexOf(id);
      if (idx === -1) selectedIds.value.push(id);
      else selectedIds.value.splice(idx, 1);
    };

    const selectAll = () => {
      if (currentGroup.value) {
        selectedIds.value = currentGroup.value.images.map(i => i.mediaId);
      }
    };

    const clearSelection = () => {
      selectedIds.value = [];
    };

    const rejectItem = async (mediaId) => {
      if (!currentGroup.value) return;
      try {
        await api.rejectColdStart({
          systemName: currentGroup.value.systemName,
          rejectedMediaIds: [mediaId]
        });
        const index = currentGroup.value.images.findIndex(img => img.mediaId === mediaId);
        if (index !== -1) {
          currentGroup.value.images.splice(index, 1);
          const selIdx = selectedIds.value.indexOf(mediaId);
          if (selIdx !== -1) selectedIds.value.splice(selIdx, 1);
        }
        currentGroup.value.totalPending--;
      } catch (error) {
        alert("排除失敗：" + (error.response?.data?.message || "權限不足"));
      }
    };

    const submitSelection = async () => {
      if (!currentGroup.value) return;
      const count = selectedIds.value.length;
      if (!confirm(`確定將這 ${count} 張設為正樣本嗎？\n(⚠️ 未勾選的將自動移入垃圾桶)`)) return;

      try {
        const allIds = currentGroup.value.images.map(img => img.mediaId);
        const rejectedIds = allIds.filter(id => !selectedIds.value.includes(id));
        const payload = {
            systemName: currentGroup.value.systemName,
            selectedMediaIds: selectedIds.value,
            rejectedMediaIds: rejectedIds 
        };
        await api.confirmColdStart(payload);
        alert(`✅ 處理完成！已成功建立特徵庫。`);
        await fetchData();
      } catch (error) {
        alert("提交失敗：" + (error.response?.data?.message || "請檢查連線"));
      }
    };

    const openFullView = (url) => fullViewImage.value = url;
    const closeFullView = () => fullViewImage.value = null;

    onMounted(() => {
      fetchPersons(); // 載入時抓取帳號選單
      fetchData();    // 載入時抓取待審核圖片
    });

    return {
      loading, pendingList, currentIndex, currentGroup, selectedIds, fullViewImage, isUploading,
      personsList, uploadTarget, myPerms, 
      openFullView, closeFullView, fetchData, switchPerson, toggleSelection, selectAll, 
      clearSelection, submitSelection, rejectItem, handleFileUpload
    };
  }
};