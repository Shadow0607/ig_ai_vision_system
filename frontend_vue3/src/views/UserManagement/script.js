import { ref, onMounted } from 'vue';
import api from '@/api_clients/api.js';
import RoleManager from '../RoleManager/index.vue';
export default {
    name: 'UserManagement',
    components: {
        RoleManager
    },
    
    setup() {
        const users = ref([]);
        const roles = ref([]);
        const isLoading = ref(false);
        const showRoleManager = ref(false);
        const showResetModal = ref(false);
        const resettingUser = ref(null);
        const newPasswordInput = ref('');

        const showModal = ref(false);
        const isSubmitting = ref(false);
        const newUser = ref({ username: '', password: '' });

        // 🌟 新增：角色權限管理的狀態
        const showRoleModal = ref(false);
        const selectedRoleId = ref('');
        const systemRoutes = ref([]);
        const editingPermissions = ref([]);
        const isSavingPerms = ref(false);

        const fetchData = async () => {
            isLoading.value = true;
            try {
                const [userRes, roleRes] = await Promise.all([
                    api.getAllUsers(),
                    api.getRoles()
                ]);
                users.value = userRes.data;
                roles.value = roleRes.data;
            } catch (error) {
                console.error("Fetch Error:", error);
            } finally {
                isLoading.value = false;
            }
        };

        const openCreateModal = () => { newUser.value = { username: '', password: '' }; showModal.value = true; };
        const closeModal = () => showModal.value = false;

        const handleCreateUser = async () => {
            isSubmitting.value = true;
            try {
                // 這裡對接您既有的註冊 API
                await api.register(newUser.value);
                alert("新增成功！");
                closeModal();
                await fetchData();
            } catch (error) { alert("新增失敗：" + (error.response?.data?.message || "")); }
            finally { isSubmitting.value = false; }
        };

        const updateUserRole = async (user) => {
            try {
                await api.updateUser(user.id, { roleId: user.roleId, isActive: user.isActive });
            }
            catch (error) {
                // 🌟 修正：優先讀取 AuthController 傳來的 error.response.data.message
                const errorMsg = error.response?.data?.message || "變更失敗";
                alert(errorMsg);

                await fetchData(); // 發生錯誤時，把畫面上選錯的下拉選單強制「彈回」原本的狀態
            }
        };

        const toggleUserStatus = async (user) => {
            try {
                await api.updateUser(user.id, { roleId: user.roleId, isActive: !user.isActive });
                user.isActive = !user.isActive;
            } catch (error) {
                // 🌟 修正：優先讀取 AuthController 傳來的 error.response.data.message
                const errorMsg = error.response?.data?.message || "操作失敗";
                alert(errorMsg);
            }
        };

        const handleDeleteUser = async (user) => {
            if (!confirm(`確定要永久刪除帳號「${user.username}」嗎？`)) return;
            try {
                await api.deleteUser(user.id);
                users.value = users.value.filter(u => u.id !== user.id);
            } catch (error) { alert("刪除失敗"); }
        };

        const formatDate = (dateStr) => {
            if (!dateStr) return 'N/A';
            const d = new Date(dateStr);
            return `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()}`;
        };

        // ==========================================
        // 🌟 新增：角色權限管理邏輯
        // ==========================================
        const openRoleModal = async () => {
            selectedRoleId.value = '';
            editingPermissions.value = [];
            showRoleModal.value = true;
            try {
                // 獲取系統所有可用頁面
                const res = await api.getSystemRoutes();
                systemRoutes.value = res.data;
            } catch (e) { console.error(e); }
        };

        const closeRoleModal = () => { showRoleModal.value = false; };

        // 當選擇不同角色時，取得該角色的既有權限
        const onRoleChange = async () => {
            if (!selectedRoleId.value) return;
            try {
                const res = await api.getRolePermissions(selectedRoleId.value);
                const existingPerms = res.data;

                // 將所有可用頁面映射到表格，並填入該角色的狀態
                editingPermissions.value = systemRoutes.value.map(route => {
                    const ext = existingPerms.find(p => p.routeId === route.id);
                    return {
                        routeId: route.id,
                        title: route.title,
                        canView: ext ? ext.canView : false,
                        canCreate: ext ? ext.canCreate : false,
                        canUpdate: ext ? ext.canUpdate : false,
                        canDelete: ext ? ext.canDelete : false
                    };
                });
            } catch (e) { console.error(e); }
        };

        const saveRolePermissions = async () => {
            try {
                isSavingPerms.value = true;
                await api.updateRolePermissions(selectedRoleId.value, editingPermissions.value);
                alert('✅ 角色權限儲存成功！下次該角色登入時即生效。');
                closeRoleModal();
            } catch (e) { alert('儲存失敗'); }
            finally { isSavingPerms.value = false; }
        };
        const openResetPasswordModal = (user) => {
            resettingUser.value = user;
            newPasswordInput.value = ''; // 清空上次輸入
            showResetModal.value = true;
        };
        const closeResetModal = () => {
            showResetModal.value = false;
            resettingUser.value = null;
        };
        const handleResetPassword = async () => {
            if (!resettingUser.value || newPasswordInput.value.length < 6) return;
            
            isSubmitting.value = true;
            try {
                await api.resetUserPassword(resettingUser.value.id, newPasswordInput.value);
                alert(`✅ 成功將「${resettingUser.value.username}」的密碼重置！`);
                closeResetModal();
            } catch (error) {
                alert("❌ 重置失敗：" + (error.response?.data?.message || "伺服器錯誤"));
            } finally {
                isSubmitting.value = false;
            }
        };

        onMounted(fetchData);

        return {
            users, roles, isLoading, showModal, isSubmitting, newUser,
            openCreateModal, closeModal, handleCreateUser, updateUserRole,
            toggleUserStatus, handleDeleteUser, formatDate,
            // 🌟 匯出權限管理方法給 Template
            showRoleModal, selectedRoleId, editingPermissions, isSavingPerms,
            openRoleModal, closeRoleModal, onRoleChange, saveRolePermissions,showRoleManager,
            showResetModal, resettingUser, newPasswordInput,
            openResetPasswordModal, closeResetModal, handleResetPassword
        };
    }
};