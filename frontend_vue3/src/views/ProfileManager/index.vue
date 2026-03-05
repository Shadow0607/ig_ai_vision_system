<template>
  <div class="profile-manager">
    <header class="header-actions">
      <h2>👤 目標人物管理</h2>
      <button v-if="myPerms.canCreate" class="btn-primary" @click="openCreatePersonModal">+ 新增追蹤人物</button>
    </header>

    <div v-if="loading" class="loading-state">資料載入中...</div>

    <div v-else class="persons-grid">
      <div v-for="person in personsList" :key="person.id" class="person-card" :class="{ 'inactive': !person.isActive }">

        <div class="card-header">
          <div v-if="person.isEditing" class="edit-header">
            <input v-model="person.tempDisplayName" class="input-edit-inline" placeholder="輸入名稱"
              @keyup.enter="savePersonName(person)" @keyup.esc="cancelEdit(person)" />
            <span class="icon-btn save" @click.stop="savePersonName(person)">💾</span>
            <span class="icon-btn cancel" @click.stop="cancelEdit(person)">❌</span>
          </div>

          <div v-else class="display-header" @click="myPerms.canUpdate ? startEdit(person) : null" :style="{ cursor: myPerms.canUpdate ? 'pointer' : 'default' }" :title="myPerms.canUpdate ? '點擊修改名稱' : ''">
            <div class="header-left-group">
              <div class="avatar-wrapper">
                <img v-if="person.currentAvatar" :src="person.currentAvatar" class="person-avatar" />
                <div v-else class="person-avatar-placeholder">👤</div>
              </div>
              <div class="name-info">
                <h3>
                  {{ person.displayName || person.systemName }}
                  <span v-if="myPerms.canUpdate" class="edit-pencil">✏️</span>
                </h3>
                <span class="system-name-badge">@{{ person.systemName }}</span>
              </div>
            </div>

            <div style="display: flex; align-items: center; gap: 10px;">
              <button v-if="myPerms.canDelete" class="icon-btn remove-btn" @click.stop="removePerson(person.id)" title="永久刪除此追蹤目標">🗑️</button>
            </div>
          </div>
        </div>

        <div class="card-body">
          <div class="control-group">
            <label>辨識門檻:</label>
            <input type="number" step="0.05" min="0" max="1" v-model="person.threshold" @change="updatePersonConfig(person)" :disabled="!myPerms.canUpdate" />
          </div>

          <div class="control-group">
            <label>狀態:</label>
            <select v-model="person.isActive" @change="updatePersonConfig(person)" :disabled="!myPerms.canUpdate">
              <option :value="true">🟢 追蹤中</option>
              <option :value="false">🔴 暫停中</option>
            </select>
          </div>

          <div class="avatars-manager">
            <div class="control-group">
              <label>頭像照片 ({{ person.avatars?.length || 0 }}/5):</label>
              <input type="file" :id="'fileInput_' + person.systemName" style="display: none" multiple accept="image/*" @change="handleFileUpload($event, person)">
              <button v-if="myPerms.canUpdate" class="icon-btn" @click.stop="triggerUpload(person.systemName)" title="上傳照片做為頭像">
                📤 上傳頭像
              </button>
            </div>
            
            <div class="avatars-gallery" v-if="person.avatars && person.avatars.length > 0">
              <div v-for="ava in person.avatars" :key="ava.id" class="avatar-thumb-box">
                <img :src="ava.url" class="avatar-thumb" />
                <button v-if="myPerms.canUpdate" class="btn-delete-thumb" @click.stop="deleteAvatar(ava.id)" title="移除此照片">✖</button>
              </div>
            </div>
          </div>

          <div class="accounts-section">
            <h4>綁定帳號 ({{ person.accounts?.length || 0 }})</h4>
            <ul class="accounts-list">
              <li v-for="acc in person.accounts" :key="acc.id">
                <div class="display-account-row">
                  <div class="acc-info" @click="myPerms.canUpdate ? openAccountModal(person, acc) : null" :style="{ cursor: myPerms.canUpdate ? 'pointer' : 'default' }">
                    <div class="acc-name-row">
                      <i :class="['platform-icon', getPlatformIcon(acc.platformCode)]"></i>
                      <strong>{{ acc.accountName || '未命名' }}</strong>
                      <span class="system-name-badge" :style="{ backgroundColor: acc.isMonitored ? '#4caf50' : '#ff5252', marginLeft: '8px', fontSize: '0.75rem' }">
                        {{ acc.isMonitored ? '🟢 追蹤中' : '🔴 暫停中' }}
                      </span>
                    </div>
                    <span class="sys-name">@{{ acc.accountIdentifier }}</span>
                  </div>

                  <div class="acc-actions">
                    <button v-if="myPerms.canUpdate" class="icon-btn" @click.stop="toggleAccountStatus(acc)" :title="acc.isMonitored ? '點擊暫停' : '點擊啟用'">
                      {{ acc.isMonitored ? '⏸️' : '▶️' }}
                    </button>
                    <button v-if="myPerms.canUpdate" class="icon-btn edit-btn" @click.stop="openAccountModal(person, acc)">✏️</button>
                    <button v-if="myPerms.canDelete" class="icon-btn remove-btn" @click.stop="removeAccount(acc.id)">✖</button>
                  </div>
                </div>
              </li>
            </ul>
            <button v-if="myPerms.canUpdate" class="btn-outline" @click="openAccountModal(person)">+ 新增社群帳號</button>
          </div>
        </div>
      </div>
    </div>

    <div v-if="showCreatePersonModal" class="modal-overlay">
      <div class="modal-content wide">
        <h3>🚀 新增追蹤目標</h3>
        <div class="form-item"><label>系統代號 (唯一):</label><input v-model="newPerson.systemName" placeholder="例如: yoona" /></div>
        <div class="form-item"><label>顯示名稱:</label><input v-model="newPerson.displayName" placeholder="例如: 林潤娥" /></div>
        <div class="form-item"><label>初始門檻:</label><input type="number" step="0.01" v-model="newPerson.threshold" /></div>
        <hr class="divider" style="margin: 15px 0; border:0; border-top:1px solid #eee;">
        <h4>🔗 初始帳號 (選填)</h4>
        <div class="form-row" style="display:flex; gap:10px;">
          <div class="form-item" style="flex:1"><label>平台:</label><select v-model="initAccount.platformId"><option v-for="p in platformOptions" :key="p.id" :value="p.id">{{ p.name }}</option></select></div>
          <div class="form-item" style="flex:1"><label>類型:</label><select v-model="initAccount.accountTypeId"><option v-for="t in accountTypeOptions" :key="t.id" :value="t.id">{{ t.name }}</option></select></div>
        </div>
        <div class="form-item"><label>帳號 ID:</label><input v-model="initAccount.identifier" placeholder="例如: yoona__lim" /></div>
        <div class="modal-actions">
          <button @click="showCreatePersonModal = false">取消</button>
          <button class="btn-confirm" @click="submitCreatePerson" :disabled="!newPerson.systemName">確認建立</button>
        </div>
      </div>
    </div>

    <div v-if="showAccountModal" class="modal-overlay">
      <div class="modal-content">
        <h3>{{ isEditingAccount ? '編輯帳號' : '新增帳號' }} - {{ activePerson?.displayName }}</h3>
        <div class="form-item"><label>平台:</label><select v-model="accountForm.platformId"><option v-for="p in platformOptions" :key="p.id" :value="p.id">{{ p.name }}</option></select></div>
        <div class="form-item"><label>類型:</label><select v-model="accountForm.accountTypeId"><option v-for="t in accountTypeOptions" :key="t.id" :value="t.id">{{ t.name }}</option></select></div>
        <div class="form-item"><label>顯示名稱 (標籤):</label><input v-model="accountForm.accountName" placeholder="例如: IG本帳" /></div>
        <div class="form-item"><label>帳號 ID (Identifier):</label><input v-model="accountForm.accountIdentifier" placeholder="例如: yoona__lim" /></div>
        <div class="modal-actions">
          <button @click="showAccountModal = false">取消</button>
          <button class="btn-confirm" @click="submitAccountForm">{{ isEditingAccount ? '儲存修改' : '確認新增' }}</button>
        </div>
      </div>
    </div>
  </div>
</template>

<script src="./script.js"></script>
<style src="./style.css" scoped></style>