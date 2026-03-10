import { createApp } from 'vue'
import App from './App.vue'
import router from './router'
import './assets/style.css'

const app = createApp(App)
app.use(router)

// 🌟 核心修正：確保初始的路由守衛 (API 驗證) 跑完後，再掛載畫面
router.isReady().then(() => {
  app.mount('#app')
})