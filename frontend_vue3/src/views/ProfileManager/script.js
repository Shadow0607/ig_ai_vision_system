/* src/views/ProfileManager/script.js */
import { ref, onMounted, computed } from 'vue';
import api from '../../api_clients/api';

export default {
  setup() {
    const personsList = ref([]);
    const loading = ref(true);
    const platformOptions = ref([]);
    const accountTypeOptions = ref([]);

    const showCreatePersonModal = ref(false);
    const showAccountModal = ref(false); 
    const activePerson = ref(null);
    const isEditingAccount = ref(false);
    const editingAccountId = ref(null);
    const newPerson = ref({ systemName: '', displayName: '', threshold: 0.45 });
    const initAccount = ref({ platformId: 2, accountTypeId: 1, identifier: '' });
    const accountForm = ref({ platformId: 2, accountName: '', accountIdentifier: '', accountTypeId: 1 });

    // 🌟 解析權限 (徹底移除 localStorage，改用路由守衛配發的安全變數)
    const myPerms = computed(() => {
      const permissions = window.__USER_PERMISSIONS__ || [];
      const p = permissions.find(item => item.routeName === 'ProfileManager');
      return p || { canView: true, canCreate: false, canUpdate: false, canDelete: false };
    });

    // =========================================
    // 2. 讀取資料 (修復大小寫相容與隨機頭像)
    // =========================================
    const fetchPersons = async () => {
      loading.value = true;
      try {
        const response = await api.getAllPersons();
        personsList.value = response.data.map(p => {
          
          // 🌟 防呆：同時支援後端傳來大寫或小寫，並統一映射為小寫的 id 與 url
          const rawAvatars = p.avatars || p.Avatars || [];
          const avatarList = rawAvatars.map(a => ({
            id: a.id || a.Id,
            url: a.url || a.Url
          }));
          
          let randomAvatar = null;
          if (avatarList.length > 0) {
            const randomIndex = Math.floor(Math.random() * avatarList.length);
            randomAvatar = avatarList[randomIndex].url;
          }
          
          return {
            ...p,
            avatars: avatarList, 
            isEditing: false,
            tempDisplayName: p.displayName || p.systemName,
            currentAvatar: randomAvatar
          };
        });
      } catch (error) { 
        console.error("無法取得資料", error); 
      } finally { 
        loading.value = false; 
      }
    };
    
    const fetchOptions = async () => {
      try {
        const [pRes, tRes] = await Promise.all([api.getPlatforms(), api.getAccountTypes()]);
        platformOptions.value = pRes.data; accountTypeOptions.value = tRes.data;
      } catch (e) { console.error(e); }
    };

    const startEdit = (person) => { person.tempDisplayName = person.displayName || person.systemName; person.isEditing = true; };
    const cancelEdit = (person) => { person.isEditing = false; };
    const savePersonName = async (person) => {
      if (!person.tempDisplayName) return alert("名稱不能為空");
      try {
        await api.updatePerson(person.id, { displayName: person.tempDisplayName, threshold: person.threshold, isActive: person.isActive });
        person.displayName = person.tempDisplayName;
        person.isEditing = false;
      } catch (e) { alert("儲存失敗"); }
    };

    const updatePersonConfig = async (person) => {
      try {
        await api.updatePerson(person.id, { displayName: person.displayName, threshold: person.threshold, isActive: person.isActive });
      } catch (e) { console.error(e); }
    };

    const openAccountModal = (person, existingAccount = null) => {
      activePerson.value = person;
      if (existingAccount) {
        isEditingAccount.value = true;
        editingAccountId.value = existingAccount.id;
        accountForm.value = { platformId: existingAccount.platformId || 2, accountTypeId: existingAccount.accountTypeId || 1, accountName: existingAccount.accountName, accountIdentifier: existingAccount.accountIdentifier };
      } else {
        isEditingAccount.value = false;
        editingAccountId.value = null;
        accountForm.value = { platformId: 2, accountTypeId: 1, accountName: '', accountIdentifier: '' };
      }
      showAccountModal.value = true;
    };

    const submitAccountForm = async () => {
      try {
        if (isEditingAccount.value) await api.updateAccount(editingAccountId.value, accountForm.value);
        else await api.addAccount(activePerson.value.id, accountForm.value);
        showAccountModal.value = false;
        fetchPersons();
      } catch (e) { alert(isEditingAccount.value ? "更新失敗" : "新增失敗"); }
    };

    const removeAccount = async (id) => {
      if (confirm("確定刪除此帳號？")) {
        try { await api.deleteAccount(id); fetchPersons(); } catch (e) { alert("刪除失敗"); }
      }
    };

    const toggleAccountStatus = async (account) => {
      const newStatus = !account.isMonitored;
      account.isMonitored = newStatus;
      try { await api.updateAccount(account.id, { isMonitored: newStatus }); } 
      catch (e) { account.isMonitored = !newStatus; alert("狀態更新失敗"); }
    };

    const openCreatePersonModal = () => {
      newPerson.value = { systemName: '', displayName: '', threshold: 0.45 };
      initAccount.value.identifier = '';
      showCreatePersonModal.value = true;
    };

    const getPlatformIcon = (code) => {
      const c = code ? code.toLowerCase() : 'default';
      switch (c) {
        case 'ig': return 'fa-brands fa-instagram';
        case 'yt': return 'fa-brands fa-youtube';
        case 'tiktok': return 'fa-brands fa-tiktok';
        case 'threads': return 'fa-brands fa-threads';
        case 'fb': return 'fa-brands fa-facebook';
        default: return 'fa-solid fa-globe';
      }
    };

    const submitCreatePerson = async () => {
      try {
        const payload = {
          systemName: newPerson.value.systemName,
          displayName: newPerson.value.displayName,
          threshold: newPerson.value.threshold,
          initialAccount: initAccount.value.identifier ? { platformId: initAccount.value.platformId, accountTypeId: initAccount.value.accountTypeId, identifier: initAccount.value.identifier } : null
        };
        await api.createPerson(payload);
        showCreatePersonModal.value = false;
        fetchPersons();
      } catch (e) { alert("建立失敗：" + (e.response?.data?.message || e.message)); }
    };

    const removePerson = async (id) => {
      if (!confirm("🔴 警告：確定要「永久刪除」此追蹤目標嗎？\n此動作會連同他綁定的社群帳號一併刪除，且無法復原！")) return;
      try {
        await api.deletePerson(id);
        alert("✅ 已成功永久刪除");
        fetchPersons();
      } catch (error) { alert("刪除失敗：" + (error.response?.data?.message || "無法連線")); }
    };

    // 🌟 上傳前先檢查前端 5 張限制
    const triggerUpload = (systemName) => { document.getElementById(`fileInput_${systemName}`)?.click(); };
    
    const handleFileUpload = async (event, person) => {
      const files = event.target.files;
      if (!files || files.length === 0) return;

      const currentCount = person.avatars ? person.avatars.length : 0;
      if (currentCount + files.length > 5) {
        alert(`❌ 數量超載限制：\n單一人物最多只能擁有 5 張特徵照片。\n目前已有 ${currentCount} 張，您試圖再傳 ${files.length} 張。\n請先刪除不要的圖片後再試！`);
        event.target.value = null; // 清空選擇
        return;
      }

      const formData = new FormData();
      for (let i = 0; i < files.length; i++) formData.append('files', files[i]);

      try {
        await api.uploadManualPhotos(person.systemName, formData);
        alert(`✅ 成功上傳 ${files.length} 個檔案！`);
        fetchPersons(); 
      } catch (error) {
        alert("❌ 上傳失敗：" + (error.response?.data?.message || "網路連線錯誤"));
      } finally {
        event.target.value = null;
      }
    };

    // 🌟 刪除單張特徵照片邏輯
    const deleteAvatar = async (mediaId) => {
      if (!mediaId) {
        alert("❌ 找不到該照片的 ID，無法執行刪除！");
        return;
      }

      if (!confirm("確定要刪除這張特徵照片嗎？")) return;
      
      try {
        await api.deleteManualPhoto(mediaId);
        fetchPersons(); // 刪除成功後重新整理畫面
      } catch(error) {
        const errorMsg = error.response?.data?.message 
                      || error.response?.data?.title 
                      || error.message;
        alert("刪除失敗：" + errorMsg);
      }
    };

    onMounted(() => { fetchPersons(); fetchOptions(); });

    return {
      personsList, loading, platformOptions, accountTypeOptions,
      showCreatePersonModal, showAccountModal, activePerson,
      newPerson, initAccount, accountForm, isEditingAccount,
      openCreatePersonModal, submitCreatePerson, removePerson,
      openAccountModal, submitAccountForm, 
      updatePersonConfig, removeAccount, getPlatformIcon,
      startEdit, cancelEdit, savePersonName, toggleAccountStatus, 
      triggerUpload, handleFileUpload, deleteAvatar, myPerms
    };
  }
};