<template>
  <div class="classified-container">
    <header class="page-header">
      <div class="header-left">
        <h2>🖼️ AI 分類結果檢視</h2>
        <select v-model="selectedAccount" @change="onAccountChange" class="account-select">
          <option value="">🌟 全部帳號 (All)</option>
          <option v-for="person in personsList" :key="person.systemName" :value="person.systemName">
            {{ person.displayName }} ({{ person.systemName }})
          </option>
        </select>
        <button 
          v-if="mediaList.length > 0" 
          class="btn-select-all" 
          @click="toggleSelectAll"
        >
          {{ isAllSelected ? '🔲 取消全選' : '☑️ 全選本頁' }}
        </button>
      </div>
      
      <div class="status-tabs">
        <button v-for="tab in tabs" :key="tab.value" :class="['tab-btn', { active: currentStatus === tab.value }]"
          @click="changeStatus(tab.value)">
          {{ tab.label }}
        </button>
      </div>
    </header>

    <div v-if="loading" class="loading">正在讀取雲端影像...</div>

    <div v-else class="image-grid" :class="{ 'has-bottom-bar': selectedIds.length > 0 }">
      <div v-for="item in mediaList" :key="item.id" class="media-card" :class="{ 'is-selected': selectedIds.includes(item.id) }">
        <div class="img-wrapper" @click="toggleSelection(item.id)">
          <template v-if="item.url.toLowerCase().endsWith('.mp4')">
            <video :src="item.url" autoplay muted loop playsinline class="media-fit"></video>
          </template>
          <template v-else>
            <img :src="item.url" loading="lazy" class="media-fit" />
          </template>
          <input type="checkbox" class="card-checkbox" :checked="selectedIds.includes(item.id)" />
          <div class="score-badge" :class="getScoreClass(item.confidenceScore)">
            {{ (item.confidenceScore * 100).toFixed(1) }}%
          </div>
          <div class="card-actions">
            <button class="action-btn view" @click.stop="openFullView(item.url)" title="檢視原圖">🔍</button>
            <button v-if="currentStatus !== 'REJECTED'" class="action-btn reject" @click.stop="reclassify(item, 6)" title="標示為錯誤並排除">🗑️</button>
            <button v-if="currentStatus !== 'OUTPUT'" class="action-btn restore" @click.stop="reclassify(item, 4)" title="拉回並確認為本人">🌟</button>
          </div>
        </div>
        <div class="media-info">
          <div class="tag-group" style="display: flex; gap: 6px; margin-bottom: 4px;">
            <span 
              class="dynamic-status-badge" 
              :style="{ backgroundColor: item.statusColor || '#6c757d' }"
            >
              {{ item.statusName || item.recognitionStatus }}
            </span>
            <span class="system-tag">{{ item.systemName }}</span>
          </div>
          <p class="file-date">{{ formatDate(item.processedAt) }}</p>
        </div>
      </div>
    </div>

    <div v-if="!loading && totalPages > 0" class="pagination-bar">
      <button class="page-btn" :disabled="currentPage === 1" @click="changePage(currentPage - 1)">⬅️ 上一頁</button>
      <span class="page-info">第 {{ currentPage }} / {{ totalPages }} 頁 (共 {{ totalItems }} 筆)</span>
      <button class="page-btn" :disabled="currentPage === totalPages" @click="changePage(currentPage + 1)">下一頁 ➡️</button>
      
      <select v-model="pageSize" @change="onPageSizeChange" class="page-size-select">
        <option :value="20">20 筆 / 頁</option>
        <option :value="50">50 筆 / 頁</option>
        <option :value="100">100 筆 / 頁</option>
      </select>
    </div>

    <div v-if="!loading && mediaList.length === 0" class="empty-state">此分類目前沒有照片。</div>

    <div v-if="selectedIds.length > 0" class="batch-action-bar">
      <span class="selected-text">已選擇 {{ selectedIds.length }} 筆項目</span>
      <div class="batch-buttons">
        <button v-if="currentStatus !== 'REJECTED'" class="btn-batch reject" @click="batchReclassify(6)">批量排除 🗑️</button>
        <button v-if="currentStatus !== 'OUTPUT'" class="btn-batch restore" @click="batchReclassify(4)">批量拉回 🌟</button>
        <button class="btn-batch cancel" @click="selectedIds = []">取消選擇</button>
      </div>
    </div>

    <div v-if="fullViewImage" class="full-view-overlay" @click.self="closeFullView">
      <template v-if="fullViewImage.toLowerCase().endsWith('.mp4')">
        <video ref="videoPlayer" :key="fullViewImage" :src="fullViewImage" controls autoplay muted loop playsinline class="full-view-content"></video>
      </template>
      <template v-else>
        <img :src="fullViewImage" class="full-view-content" />
      </template>
    </div>
  </div>
</template>
<script src="./script.js"></script>
<style src="./style.css" scoped></style>