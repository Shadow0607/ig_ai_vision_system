<template>
  <div class="page-container">
    <div class="page-header">
      <h2 class="section-title">🎯 人工覆核中心 <span class="sub-desc">處理 AI 邊緣特徵數據</span></h2>
    </div>

    <div class="review-layout">
      <div class="main-gallery">
        <div class="gallery-header">
          <div class="gallery-info">
            <h3>待審核名單 ({{ pendingImages.length }})</h3>
            <span class="selection-status">已勾選 <strong>{{ selectedIds.length }}</strong> 張</span>
          </div>
          <div class="gallery-actions" v-if="myPerms.hasAction('UPDATE')">
            <button class="btn-outline" @click="selectAll">本頁全選</button>
            <button class="btn-outline" @click="clearSelection">取消選取</button>
          </div>
        </div>

        <div class="image-grid">
          <div v-for="img in pendingImages" :key="img.id" class="img-card"
            :class="{ 'selected': selectedIds.includes(img.id), 'readonly-mode': !myPerms.hasAction('UPDATE') }"
            @click="myPerms.hasAction('UPDATE') ? toggleSelection(img.id) : null">

            <div class="media-wrapper">
              <template v-if="img.url.toLowerCase().endsWith('.mp4')">
                <video :src="img.url" :poster="img.posterUrl" autoplay muted loop playsinline
                  class="media-content"></video>
                <div class="video-badge">▶ 影片</div>
              </template>

              <template v-else>
                <img :src="img.url" loading="lazy" class="media-content" />
              </template>

              <button class="btn-trash" @click.stop="handleReject(img.id)" title="排除此樣本">🗑️</button>

              <button class="btn-view-full" @click.stop="openFullView(img.url)" title="檢視原圖">🔍</button>

              <div class="overlay">✔</div>
            </div>

            <div class="person-info">
              <span class="person-name" :title="img.personName">👤 {{ img.personName }}</span>
              <span class="status-badge" 
                    v-if="img.statusName" 
                    :style="{ backgroundColor: img.statusColor || '#777' }">
                {{ img.statusName }}
              </span>
            </div>

            <div class="score-container">
              <div class="score-label">AI 相似度: {{ (img.score * 100).toFixed(1) }}%</div>
              <div class="score-bar-bg">
                <div class="score-bar-fill"
                  :style="{ width: `${img.score * 100}%`, backgroundColor: getScoreColor(img.score) }">
                </div>
              </div>
            </div>

          </div>
        </div>

        <div class="gallery-footer">
          <div class="hint">💡 提示：只勾選確認為本人的照片，系統將自動提取特徵並強化 AI 模型。</div>
          <button class="btn-confirm" @click="submitBatchApproval" :disabled="selectedIds.length === 0">
            確認疊加所選特徵 ({{ selectedIds.length }}) 🚀
          </button>
        </div>
      </div>
    </div>

    <div v-if="fullViewImage" class="full-view-overlay" @click.self="closeFullView">
      <template v-if="fullViewImage.toLowerCase().endsWith('.mp4')">
        <video :src="fullViewImage" controls autoplay muted loop playsinline class="full-view-content"></video>
      </template>
      <template v-else>
        <img :src="fullViewImage" class="full-view-content" alt="Full view" />
      </template>
      <button class="btn-close-full" @click="closeFullView">×</button>
    </div>

  </div>
</template>

<script src="./script.js"></script>
<style src="./style.css" scoped></style>