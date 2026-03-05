<template>
  <div class="user-mgmt-container">
    <header class="mgmt-header">
      <div class="header-content">
        <h2>帳號權限管理系統</h2>
        <p class="subtitle">管理員可在此調整用戶的角色等級 (RoleId) 與存取狀態 (IsActive)。</p>
      </div>
      <div class="header-right">
        <div class="header-stats">總帳號數: <strong>{{ users.length }}</strong></div>
        <button class="btn-role-alt" @click="showRoleManager = true">🎭 角色管理</button>
        <button class="btn-role" @click="openRoleModal">🛡️ 角色權限設定</button>
        <button class="btn-create" @click="openCreateModal">➕ 新增使用者</button>
      </div>
    </header>

    <div v-if="showModal" class="modal-overlay" @click.self="closeModal">
      <div class="modal-card">
        <div class="modal-header">
          <h3>新增系統帳號</h3>
          <button class="close-icon" @click="closeModal">×</button>
        </div>
        <form @submit.prevent="handleCreateUser" class="modal-body">
          <div class="form-group">
            <label>帳號名稱 (Username)</label>
            <input v-model="newUser.username" type="text" placeholder="輸入登入帳號..." required />
          </div>
          <div class="form-group">
            <label>登入密碼 (Password)</label>
            <input v-model="newUser.password" type="password" placeholder="輸入初始密碼..." required />
          </div>
          <div class="modal-footer">
            <button type="button" class="btn-cancel" @click="closeModal">取消</button>
            <button type="submit" class="btn-confirm" :disabled="isSubmitting">確認新增</button>
          </div>
        </form>
      </div>
    </div>

    <div v-if="showRoleManager" class="modal-overlay">
      <div class="modal-card role-modal-card">
        <div class="modal-header">
          <h3>系統角色設定</h3>
          <button class="close-btn" @click="showRoleManager = false">✖</button>
        </div>

        <RoleManager />

      </div>
    </div>
    <div v-if="showResetModal" class="modal-overlay" @click.self="closeResetModal">
  <div class="modal-card">
    <div class="modal-header">
      <h3>🔑 重置密碼 - {{ resettingUser?.username }}</h3>
      <button class="close-icon" @click="closeResetModal">×</button>
    </div>
    <form @submit.prevent="handleResetPassword" class="modal-body">
      <div class="form-group">
        <label>設定新密碼</label>
        <input v-model="newPasswordInput" type="text" placeholder="請輸入新密碼..." required minlength="6" />
        <small style="color:#aaa; display:block; margin-top:5px;">新密碼將會直接覆蓋原密碼。</small>
      </div>
      <div class="modal-footer">
        <button type="button" class="btn-cancel" @click="closeResetModal">取消</button>
        <button type="submit" class="btn-confirm" :disabled="isSubmitting || newPasswordInput.length < 6">確認重置</button>
      </div>
    </form>
  </div>
   </div>

    <div v-if="showRoleModal" class="modal-overlay" @click.self="closeRoleModal">
      <div class="modal-card role-modal-card">
        <div class="modal-header">
          <h3>🛡️ 角色細部權限設定</h3>
          <button class="close-icon" @click="closeRoleModal">×</button>
        </div>
        <div class="modal-body">
          <div class="form-group">
            <label>選擇要設定的角色</label>
            <select v-model="selectedRoleId" @change="onRoleChange" class="role-select">
              <option value="" disabled>請選擇角色...</option>
              <option v-for="role in roles" :key="role.id" :value="role.id">
                {{ role.name }} ({{ role.code }})
              </option>
            </select>
          </div>

          <div v-if="selectedRoleId" class="permissions-table-container">
            <table class="permissions-table">
              <thead>
                <tr>
                  <th>頁面功能</th>
                  <th>檢視 <br><small>(View)</small></th>
                  <th>新增 <br><small>(Create)</small></th>
                  <th>修改 <br><small>(Update)</small></th>
                  <th>刪除 <br><small>(Delete)</small></th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="perm in editingPermissions" :key="perm.routeId">
                  <td class="route-title">{{ perm.title }}</td>
                  <td><input type="checkbox" v-model="perm.canView" /></td>
                  <td><input type="checkbox" v-model="perm.canCreate" /></td>
                  <td><input type="checkbox" v-model="perm.canUpdate" /></td>
                  <td><input type="checkbox" v-model="perm.canDelete" /></td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
        <div class="modal-footer" v-if="selectedRoleId">
          <button class="btn-cancel" @click="closeRoleModal">取消</button>
          <button class="btn-confirm" @click="saveRolePermissions" :disabled="isSavingPerms">
            {{ isSavingPerms ? '儲存中...' : '儲存設定' }}
          </button>
        </div>
      </div>
    </div>

    <div class="table-container">
      <table class="user-table">
        <thead>
          <tr>
            <th>帳號名稱</th>
            <th>角色權限</th>
            <th>帳號狀態</th>
            <th>建立時間</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="user in users" :key="user.id">
            <td>
              <div class="user-info"><span class="avatar">👤</span>{{ user.username }}</div>
            </td>
            <td>
              <select v-model="user.roleId" @change="updateUserRole(user)" class="role-select-inline">
                <option v-for="role in roles" :key="role.id" :value="role.id">
                  {{ role.name }} ({{ role.code }})
                </option>
              </select>
            </td>
            <td>
              <span :class="['status-badge', user.isActive ? 'active' : 'inactive']">
                {{ user.isActive ? '啟用中' : '已停用' }}
              </span>
            </td>
            <td class="date-cell">{{ formatDate(user.createdAt) }}</td>
            <td>
              <div class="action-group">
                <button @click="openResetPasswordModal(user)" class="btn-icon btn-reset" title="重置密碼"
                  :disabled="isLoading">
                  🔑
                </button>
                <button @click="toggleUserStatus(user)"
                  :class="['btn-icon', user.isActive ? 'btn-deactivate' : 'btn-activate']"
                  :title="user.isActive ? '停用帳號' : '恢復啟用'" :disabled="isLoading || user.username === 'admin'">
                  {{ user.isActive ? '🚫' : '✅' }}
                </button>
                <button @click="handleDeleteUser(user)" class="btn-icon btn-delete" title="永久刪除"
                  :disabled="isLoading || user.username === 'admin'">
                  🗑️
                </button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-if="users.length === 0 && !isLoading" class="empty-state">目前系統中無其他使用者資料。</div>
    </div>
  </div>
</template>
<script src="./script.js"></script>
<style src="./style.css" scoped></style>