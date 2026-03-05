<template>
  <div class="role-manager-container">
    <header class="role-header">
      <h3 class="role-title">🎭 角色定義管理</h3>
      <button class="btn-primary" @click="openRoleModal()">+ 新增角色</button>
    </header>

    <div class="admin-table-wrapper">
      <table class="admin-table">
        <thead>
          <tr>
            <th>ID</th>
            <th>角色名稱</th>
            <th>代碼 (Code)</th>
            <th>成員數</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="role in roles" :key="role.id">
            <td>{{ role.id }}</td>
            <td><strong>{{ role.name }}</strong></td>
            <td><span class="code-badge">{{ role.code }}</span></td>
            <td>{{ role.userCount ?? role.UserCount ?? 0 }} 人</td>
            
            <td class="action-cell">
              <button class="action-btn edit-btn" @click="openRoleModal(role)">✏️ 編輯</button>
              <button v-if="role.id > 3" class="action-btn delete-btn" @click="handleDeleteRole(role)">🗑️ 刪除</button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="showModal" class="modal-overlay">
      <div class="modal-content">
        <h3>{{ isEdit ? '編輯角色' : '新增角色' }}</h3>
        <div class="form-item">
          <label>顯示名稱:</label>
          <input v-model="roleForm.name" placeholder="例如：超級管理員" />
        </div>
        <div class="form-item">
          <label>角色代碼 (唯一):</label>
          <input v-model="roleForm.code" :disabled="isEdit" placeholder="例如：SUPER_ADMIN" />
          <small v-if="isEdit" class="hint-text">角色代碼建立後不可修改</small>
        </div>
        <div class="modal-actions">
          <button class="btn-cancel" @click="showModal = false">取消</button>
          <button class="btn-confirm" @click="submitRole">確認儲存</button>
        </div>
      </div>
    </div>
  </div>
</template>

<script src="./script.js"></script>
<style src="./style.css" scoped></style>