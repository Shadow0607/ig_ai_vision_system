<template>
  <div class="page-container">
    <header class="page-header">
      <div class="header-content">
        <h2 class="section-title">❄️ 冷啟動 (Cold Start) 建檔</h2>
        <p class="page-desc">
          系統自動捕捉待確認的照片。請勾選 <strong>清晰、正確</strong> 的照片，AI 將以此為基準學習。
          <span class="sub-desc">(未勾選的照片將自動視為雜訊排除)</span>
        </p>
      </div>

      <div class="header-actions" v-if="myPerms.canUpdate">
        <select v-model="uploadTarget" class="account-select">
          <option value="" disabled>-- 選擇要手動上傳的帳號 --</option>
          <option v-for="person in personsList" :key="person.systemName" :value="person.systemName">
            {{ person.displayName }} ({{ person.systemName }})
          </option>
        </select>
        
        <input type="file" ref="fileInputPos" multiple accept="image/*,video/mp4" style="display: none" @change="handleFileUpload($event, true)" />
        <input type="file" ref="fileInputNeg" multiple accept="image/*,video/mp4" style="display: none" @change="handleFileUpload($event, false)" />
        
        <button class="btn-upload pos" @click="$refs.fileInputPos.click()" :disabled="!uploadTarget || isUploading">➕ 上傳正樣本</button>
        <button class="btn-upload neg" @click="$refs.fileInputNeg.click()" :disabled="!uploadTarget || isUploading">➖ 上傳負樣本</button>
      </div>
    </header>

    <div v-if="loading" class="loading-state">
      <div class="spinner"></div>
      <p>正在撈取待審核樣本...</p>
    </div>

    <div v-else-if="pendingList.length === 0" class="empty-state">
      <div class="empty-icon">🎉</div>
      <h3>目前沒有待審核項目</h3>
      <p>所有特徵庫都已建立，或爬蟲尚未抓到新樣本。</p>
      <button class="btn-text" style="margin-top:15px" @click="fetchData">重新整理</button>
    </div>

    <div v-else class="review-layout">
      
      <aside class="sidebar-person">
        <div class="sidebar-title">待審核名單 ({{ pendingList.length }})</div>
        <ul class="person-list">
          <li 
            v-for="(group, index) in pendingList" 
            :key="group.systemName"
            class="person-item"
            :class="{ 'active': currentIndex === index }"
            @click="switchPerson(index)"
          >
            <div class="person-info">
              <span class="name">{{ group.displayName }}</span>
              <span class="sys-name">{{ group.systemName }}</span>
            </div>
            <span class="count-badge">{{ group.totalPending }}</span>
          </li>
        </ul>
      </aside>

      <main class="main-gallery" v-if="currentGroup">
        <div class="gallery-header">
          <div class="gallery-info">
            <h3>{{ currentGroup.displayName }} <small>({{ currentGroup.systemName }})</small></h3>
            <p class="selection-status">
              已勾選 <strong>{{ selectedIds.length }}</strong> 張 / 展示前 {{ currentGroup.images.length }} 張
            </p>
          </div>
          <div class="gallery-actions" v-if="myPerms.canUpdate">
            <button class="btn-text" @click="selectAll">本頁全選</button>
            <button class="btn-text" @click="clearSelection">全不選</button>
          </div>
        </div>

        <div class="image-grid">
          <div 
            v-for="img in currentGroup.images" 
            :key="img.mediaId"
            class="img-card" 
            :class="{ 
              'selected': selectedIds.includes(img.mediaId), 
              'readonly-mode': !myPerms.canUpdate 
            }"
            @click="myPerms.canUpdate ? toggleSelection(img.mediaId) : null"
          >
            <div class="media-wrapper">
              <template v-if="img.fileName.toLowerCase().endsWith('.mp4')">
                <video 
                  :src="img.url"
                  :poster="img.posterUrl" 
                  autoplay muted loop playsinline
                  class="media-content"
                ></video>
                <div class="video-badge">▶ 影片</div>
              </template>
              
              <template v-else>
                <img :src="img.url" loading="lazy" class="media-content" />
              </template>

              <div class="overlay">
                <span class="check-icon">✔</span>
              </div>

              <button 
                  v-if="myPerms.canDelete"
                  class="btn-trash" 
                  @click.stop="rejectItem(img.mediaId)" 
                  title="排除此樣本"
              >
                  🗑️
              </button>

              <button 
                  class="btn-view-full"
                  @click.stop="openFullView(img.url)"
                  title="檢視原圖"
              >
                  🔍
              </button>

            </div>

            <div class="file-name" :title="img.fileName">{{ img.fileName }}</div>
          </div>
        </div>

        <div class="gallery-footer" v-if="myPerms.canUpdate">
          <p class="hint">💡 提示：只勾選正確照片，確認後將移動檔案並建立特徵庫。</p>
          <button 
            class="btn-confirm" 
            @click="submitSelection" 
            :disabled="selectedIds.length === 0"
          >
            確認並建立特徵庫 ({{ selectedIds.length }}) 🚀
          </button>
        </div>
      </main>
    </div>

    <div v-if="fullViewImage" class="full-view-overlay" @click.self="closeFullView">
      <template v-if="fullViewImage.toLowerCase().endsWith('.mp4')">
         <video 
           :src="fullViewImage" 
           controls 
           autoplay 
           muted 
           loop 
           playsinline 
           class="full-view-content"
         ></video>
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