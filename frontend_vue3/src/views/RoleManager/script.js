
import { ref, onMounted } from 'vue';
import api from '../../api_clients/api';

export default {
  setup() {
    const roles = ref([]);
    const showModal = ref(false);
    const isEdit = ref(false);
    const roleForm = ref({ id: null, name: '', code: '' });

    const fetchRoles = async () => {
      const res = await api.getRoles();
      roles.value = res.data;
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
        if (isEdit.value) {
          await api.updateRole(roleForm.value.id, roleForm.value);
        } else {
          await api.createRole(roleForm.value);
        }
        showModal.value = false;
        fetchRoles();
      } catch (e) {
        alert("儲存失敗：" + e.response?.data || e.message);
      }
    };

    // 在 handleDeleteRole 方法中，把判斷式更新：
    const handleDeleteRole = async (role) => {
      // 🌟 改用 userCount 來判斷
      if (role.userCount > 0) return alert("該角色尚有成員，無法刪除！");

      if (!confirm(`確定要刪除角色「${role.name}」嗎？`)) return;

      await api.deleteRole(role.id);
      fetchRoles();
    };

    onMounted(fetchRoles);
    return { roles, showModal, roleForm, isEdit, openRoleModal, submitRole, handleDeleteRole };
  }
}


