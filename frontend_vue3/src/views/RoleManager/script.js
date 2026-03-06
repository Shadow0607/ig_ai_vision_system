import { ref, onMounted } from 'vue';
import api from '../../api_clients/api';
import { usePermissions } from '@/composables/usePermissions';

export default {
  setup() {
    const roles = ref([]);
    const showModal = ref(false);
    const isEdit = ref(false);
    const roleForm = ref({ id: null, name: '', code: '' });

    const fetchRoles = async () => {
      try {
        const res = await api.getRoles();
        roles.value = res.data;
      } catch (e) {
        console.error("獲取角色列表失敗:", e);
      }
    };

    const openRoleModal = (role = null) => {
      if (role) {
        isEdit.value = true;
        roleForm.value = { ...role };
      } else {
        isEdit.value = false;
        roleForm.value = { id: null, name: '', code: '' };
      }
      showModal.value = true;
    };

    const submitRole = async () => {
      try {
        // 🌟 核心修正 1：準備一個乾淨的 payload，只包含 name 和 code
        const payload = {
          name: roleForm.value.name,
          code: roleForm.value.code
        };

        if (isEdit.value) {
          // 修改時：把 ID 放在 URL，Body 則看後端需求（這裡一併把 id 加上以防萬一）
          await api.updateRole(roleForm.value.id, { id: roleForm.value.id, ...payload });
        } else {
          // 🌟 新增時：絕對不傳 ID，只傳 payload
          await api.createRole(payload);
        }
        
        showModal.value = false;
        fetchRoles();
      } catch (e) {
        // 🌟 核心修正 2：精準抓取後端回傳的錯誤訊息，不再顯示 [object Object]
        const errorData = e.response?.data;
        const errMsg = errorData?.message || errorData?.title || e.message || "發生未知錯誤";
        
        alert("儲存失敗：" + errMsg);
        console.error("API 錯誤詳情:", errorData); // 在 F12 Console 印出完整錯誤方便除錯
      }
    };

    const handleDeleteRole = async (role) => {
      if (role.userCount > 0) return alert("該角色尚有成員，無法刪除！");

      if (!confirm(`確定要刪除角色「${role.name}」嗎？`)) return;

      try {
        await api.deleteRole(role.id);
        fetchRoles();
      } catch (e) {
        const errMsg = e.response?.data?.message || e.message;
        alert("刪除失敗：" + errMsg);
      }
    };

    onMounted(fetchRoles);
    
    return { roles, showModal, roleForm, isEdit, openRoleModal, submitRole, handleDeleteRole };
  }
}