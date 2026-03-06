import { ref, onMounted } from 'vue';
import api from '@/api_clients/api.js';
import RoleManager from '../RoleManager/index.vue';
import { usePermissions } from '@/composables/usePermissions';

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
        const { myPerms } = usePermissions('UserManagement');

        const showModal = ref(false);
        const isSubmitting = ref(false);
        const newUser = ref({ username: '', password: '' });

        // 🌟 角色權限管理的狀態
        const showRoleModal = ref(false);
        const selectedRoleId = ref('');
        const systemRoutes = ref([]);
        const editingPermissions = ref([]);
        const isSavingPerms = ref(false);
        const availableActions = ref([]);

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
                const errorMsg = error.response?.data?.message || "變更失敗";
                alert(errorMsg);
                await fetchData(); 
            }
        };

        const toggleUserStatus = async (user) => {
            try {
                await api.updateUser(user.id, { roleId: user.roleId, isActive: !user.isActive });
                user.isActive = !user.isActive;
            } catch (error) {
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
        // 🌟 角色權限管理邏輯 (移除重複宣告)
        // ==========================================
        const openRoleModal = async () => {
            selectedRoleId.value = '';
            editingPermissions.value = [];
            showRoleModal.value = true;
            try {
                // 同時抓取「系統路由清單」和「系統可用動作」
                const [routesRes, actionsRes] = await Promise.all([
                    api.getSystemRoutes(),
                    api.getSysActions() // 🌟 呼叫剛寫好的 API
                ]);
                systemRoutes.value = routesRes.data;
                availableActions.value = actionsRes.data; // 🌟 把後端回傳的動態陣列塞進去
            } catch (e) { console.error(e); }
        };

        const closeRoleModal = () => { showRoleModal.value = false; };

        const onRoleChange = async () => {
            if (!selectedRoleId.value) return;
            try {
                const res = await api.getRolePermissions(selectedRoleId.value);
                const existingPerms = res.data;

                editingPermissions.value = systemRoutes.value.map(route => {
                    const ext = existingPerms.find(p => p.routeId === route.id);
                    return {
                        routeId: route.id,
                        title: route.title,
                        allowedActionIds: ext ? (ext.allowedActionIds || []) : [] 
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
            newPasswordInput.value = ''; 
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
            myPerms,users, roles, isLoading, showModal, isSubmitting, newUser,
            openCreateModal, closeModal, handleCreateUser, updateUserRole,
            toggleUserStatus, handleDeleteUser, formatDate,
            showRoleModal, selectedRoleId, editingPermissions, isSavingPerms,
            openRoleModal, closeRoleModal, onRoleChange, saveRolePermissions,showRoleManager,
            showResetModal, resettingUser, newPasswordInput,availableActions,
            openResetPasswordModal, closeResetModal, handleResetPassword
        };
    }
};