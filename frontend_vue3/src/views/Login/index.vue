<template>
  <div class="login-container">
    <div class="login-box">
      <header class="login-header">
        <div class="logo-icon">AI</div>
        <h2>{{ isLoginMode ? '系統存取控制' : '建立新存取金鑰' }}</h2>
        <p class="subtitle">
          {{ isLoginMode ? 'Vision System Identity Verification' : 'Register New AI System Account' }}
        </p>
      </header>

      <form @submit.prevent="handleSubmit" class="login-form">
        <div class="form-group">
          <label>{{ isLoginMode ? '管理員帳號 / Administrator ID' : '設定新帳號 / New Account ID' }}</label>
          <input 
            type="text" 
            v-model="credentials.username" 
            placeholder="請輸入帳號"
            autocomplete="off"
            :disabled="isLoading"
            required
          >
        </div>

        <div class="form-group">
          <label>{{ isLoginMode ? '存取金鑰 / Access Key' : '設定新密碼 / New Password' }}</label>
          <input 
            type="password" 
            v-model="credentials.password" 
            placeholder="••••••••"
            autocomplete="new-password"
            :disabled="isLoading"
            required
          >
        </div>

        <div class="form-group captcha-group">
          <label>安全驗證 / Security Check</label>
          <div class="captcha-row">
            <input 
              type="text" 
              v-model="credentials.captcha" 
              placeholder="輸入右圖文字"
              maxlength="4"
              class="captcha-input"
              :disabled="isLoading"
              required
            >
            <canvas 
              ref="captchaCanvas" 
              width="120" 
              height="44" 
              class="captcha-img"
              @click="refreshCaptcha" 
              title="看不清楚？點擊刷新"
            ></canvas>
          </div>
        </div>

        <div v-if="errorMsg" class="error-banner">
          ⚠️ {{ errorMsg }}
        </div>
        
        <div v-if="successMsg" class="success-banner">
          {{ successMsg }}
        </div>

        <button type="submit" class="btn-login" :disabled="isLoading">
          <span v-if="isLoading">處理中 / Processing...</span>
          <span v-else>{{ isLoginMode ? '確認登入 / LOGIN' : '註冊帳號 / REGISTER' }}</span>
        </button>
        
        <div class="toggle-mode-container">
          <a href="#" @click.prevent="toggleMode" class="toggle-link">
            {{ isLoginMode ? '還沒有帳號？點此申請註冊' : '已有帳號？返回系統登入' }}
          </a>
        </div>

        <div class="divider" v-if="isLoginMode">
          <span>OR</span>
        </div>

        <button type="button" class="btn-google" @click="handleGoogleLogin" v-if="isLoginMode">
          <svg class="google-icon" viewBox="0 0 24 24">
            <path d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z" fill="#4285F4"/>
            <path d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" fill="#34A853"/>
            <path d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" fill="#FBBC05"/>
            <path d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z" fill="#EA4335"/>
          </svg>
          Sign in with Google
        </button>
      </form>

      <footer class="login-footer">
        系統版本 v1.0.0 Stable | 受監控的網路環境
      </footer>
    </div>
  </div>
</template>

<script src="./script.js"></script>
<style src="./style.css" scoped></style>