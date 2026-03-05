<template>
  <div class="page-container dashboard-layout">
    <header class="dashboard-header">
      <div class="live-indicator">
        <span class="pulse-dot"></span>
        <span class="status-text">SYSTEM ONLINE</span>
      </div>
      <h2 class="section-title">📊 系統監控大盤 (Real-time)</h2>
      <div class="last-update">最後更新: {{ lastUpdateTime }}</div>
    </header>

    <div class="stats-grid">
      <div class="stat-card success-card">
        <div class="stat-icon">✅</div>
        <div class="stat-content">
          <span class="stat-label">辨識成功數 (Pos)</span>
          <span class="stat-value">{{ statistics.successCount }}</span>
        </div>
      </div>

      <div class="stat-card warning-card">
        <div class="stat-icon">⏭️</div>
        <div class="stat-content">
          <span class="stat-label">跳過/失敗數 (Skip)</span>
          <span class="stat-value">{{ statistics.skipCount }}</span>
        </div>
      </div>

      <div class="stat-card info-card">
        <div class="stat-icon">📈</div>
        <div class="stat-content">
          <span class="stat-label">AI 命中率</span>
          <span class="stat-value">{{ successRatio }}<small>%</small></span>
        </div>
        <div class="progress-bar-bg">
          <div class="progress-bar-fill" :style="{ width: successRatio + '%' }"></div>
        </div>
      </div>
    </div>

    <div class="monitor-console">
      <div class="console-header">
        <span class="console-title">🚨 System Alerts / Logs</span>
        <span class="console-badge">{{ alerts.length }} Active</span>
      </div>
      
      <div class="console-body">
        <div v-if="alerts.length === 0" class="empty-state">
          <span class="check-icon">✨</span>
          <p>系統運行良好，目前無異常告警。</p>
        </div>
        
        <ul v-else class="alert-list">
          <li v-for="(alert, index) in alerts" :key="index" class="alert-item animate-slide-in">
            <span class="alert-time">[{{ alert.timestamp }}]</span>
            <span class="alert-tag" :class="alert.type">{{ alert.source_component || 'SYSTEM' }}</span>
            <span class="alert-msg">{{ alert.message }}</span>
          </li>
        </ul>
      </div>
    </div>
  </div>
</template>

<script src="./script.js"></script>
<style src="./style.css" scoped></style>