<template>
  <div class="hitl-dashboard-container">
    
    <!-- 🌟 保留原有的全螢幕預覽 Modal -->
    <div v-if="fullViewImage" class="full-view-overlay" @click="closeFullView">
      <img :src="fullViewImage" class="full-view-img" @click.stop />
      <button class="close-btn" @click="closeFullView">✖</button>
    </div>

    <header class="dashboard-header">
      <h2>🎯 終極人機協作中心 (Unified HITL)</h2>
      <p>請選擇您要進行的審核任務：特徵庫批次訓練覆核，或轉發來源放行。</p>
      
      <!-- 雙軌任務頁籤切換 -->
      <div class="tabs">
        <button 
          :class="['tab-btn', { active: activeTab === 'ai-review' }]" 
          @click="activeTab = 'ai-review'">
          🧠 AI 辨識覆核區 
          <span class="badge-count" v-if="pendingImages.length">{{ pendingImages.length }}</span>
        </button>
        <button 
          :class="['tab-btn', { active: activeTab === 'repost-review' }]" 
          @click="activeTab = 'repost-review'">
          🛡️ 未知轉發審核區 
          <span class="badge-count" v-if="repostPendingList.length">{{ repostPendingList.length }}</span>
        </button>
      </div>
    </header>

    <!-- ========================================================= -->
    <!-- 頁籤 1：您原本的 AI 邊緣值覆核區 (支援批次選取與預覽) -->
    <!-- ========================================================= -->
    <div v-show="activeTab === 'ai-review'">
      <div class="batch-actions" v-if="pendingImages.length > 0 && canReview">
        <button class="btn btn-secondary" @click="selectAll">全選</button>
        <button class="btn btn-secondary" @click="clearSelection">清除</button>
        <button class="btn btn-keep batch-submit" @click="submitBatchApproval" :disabled="selectedIds.length === 0">
          ✅ 批次通過 (共 {{ selectedIds.length }} 筆)
        </button>
      </div>

      <div class="masonry-grid">
        <div 
          class="card ai-card" 
          v-for="img in pendingImages" 
          :key="'ai_'+img.id"
          :class="{ selected: selectedIds.includes(img.id) }"
          @click="toggleSelection(img.id)"
        >
          <div class="media-container">
            <!-- 點擊圖片放大預覽，阻止事件冒泡避免觸發卡片選取 -->
            <img :src="apiBaseUrl + img.fileUrl" loading="lazy" @click.stop="openFullView(apiBaseUrl + img.fileUrl)" />
            
            <!-- 依據分數動態給色 -->
            <span class="badge" :style="{ backgroundColor: getScoreColor(img.confidenceScore) }">
              {{ (img.confidenceScore * 100).toFixed(1) }}%
            </span>
            
            <!-- 選取狀態打勾圖示 -->
            <div class="check-overlay" v-if="selectedIds.includes(img.id)">✓</div>
          </div>
          
          <div class="info-section">
            <div class="source-info">
              <span class="username">{{ item.originalUsername }}</span>
              <span v-if="item.sourceIsVerified" class="verified-badge" title="官方藍勾勾">☑️</span>
            </div>

            <div v-if="item.originalUsername && item.originalUsername !== item.systemName" class="repost-badge">
              🔁 轉發自: @{{ item.originalUsername }}
            </div>
            
            <div v-if="item.reviewedBy" class="reviewer-badge">
              👨‍💻 審核員: {{ item.reviewedBy }}
            </div>

            <div class="shortcode-info" style="margin-top: 8px;">
              <small>{{ item.originalShortcode }}</small>
              <small class="time">{{ formatDate(item.createdAt) }}</small>
            </div>
          </div>
          
          <div class="action-section" v-if="canReview">
            <button class="btn btn-delete" @click.stop="handleReject(img.id)">
              ❌ 排除 (非本人)
            </button>
          </div>
        </div>
      </div>
      <div class="empty-state" v-if="pendingImages.length === 0">🎉 目前沒有需要覆核的 AI 判定！</div>
    </div>

    <!-- ========================================================= -->
    <!-- 頁籤 2：我們新增的轉發與限動隔離審核區 (單筆決斷) -->
    <!-- ========================================================= -->
    <div v-show="activeTab === 'repost-review'">
      <div class="masonry-grid">
        <div class="card" v-for="item in repostPendingList" :key="'repost_'+item.id">
          
          <div class="media-container">
            <span v-if="item.sourceTypeId === 31" class="badge badge-story">⏳ 限動 (已隔離)</span>
            <span v-else class="badge badge-post">📌 貼文/短影音</span>
            <!-- 若是 IG URL 直接顯示，若是 MinIO 內部路徑則可能需要接 API，這裡假設有完整 URL -->
            <img :src="item.thumbnailUrl" loading="lazy" @click.stop="openFullView(item.thumbnailUrl)" />
          </div>

          <div class="info-section">
            <div class="source-info">
              <span class="username">{{ item.originalUsername }}</span>
              <span v-if="item.sourceIsVerified" class="verified-badge" title="官方藍勾勾">☑️</span>
            </div>
            <div v-if="item.originalUsername && item.originalUsername !== item.systemName" class="repost-badge">
              🔁 轉發自: @{{ item.originalUsername }}
            </div>
            
            <div v-if="item.reviewedBy" class="reviewer-badge">
              👨‍💻 審核員: {{ item.reviewedBy }}
            </div>
            <div class="shortcode-info">
              <small>{{ item.originalShortcode }}</small>
              <small class="time">{{ formatDate(item.createdAt) }}</small>
            </div>
          </div>

          <div class="action-section" v-if="canReview">
            <button class="btn btn-keep" @click="decideRepost(item.id, 'KEEP')" :disabled="isProcessing === item.id">
              ✅ 放行 (下載)
            </button>
            <button class="btn btn-delete" @click="decideRepost(item.id, 'DELETE')" :disabled="isProcessing === item.id">
              🗑️ 捨棄
            </button>
          </div>

        </div>
      </div>
      <div class="empty-state" v-if="repostPendingList.length === 0">🎉 目前沒有未知轉發需要審核！</div>
    </div>

  </div>
</template>

<script src="./script.js"></script>
<style src="./style.css" scoped></style>