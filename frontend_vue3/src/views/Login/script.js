/* src/views/Login/script.js */
import { reactive, ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import api from '@/api_clients/api.js';

export default {
  name: 'Login',
  setup() {
    const router = useRouter();
    const isLoading = ref(false);
    const errorMsg = ref('');
    const successMsg = ref(''); // 🌟 新增：用來顯示註冊成功的綠色提示
    const captchaCanvas = ref(null);
    const realCaptchaCode = ref('');

    // 🌟 新增：控制當前是登入還是註冊模式
    const isLoginMode = ref(true);

    const credentials = reactive({
      username: '',
      password: '',
      captcha: ''
    });

    const randomNum = (min, max) => Math.floor(Math.random() * (max - min) + min);
    const randomColor = (min, max) => `rgb(${randomNum(min, max)},${randomNum(min, max)},${randomNum(min, max)})`;

    const drawCaptcha = () => {
      const canvas = captchaCanvas.value;
      if (!canvas) return;
      const ctx = canvas.getContext('2d');
      const width = canvas.width;
      const height = canvas.height;

      ctx.fillStyle = randomColor(200, 240);
      ctx.fillRect(0, 0, width, height);

      const pool = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
      let generatedCode = '';

      for (let i = 0; i < 4; i++) {
        const text = pool[randomNum(0, pool.length)];
        generatedCode += text;
        ctx.font = 'bold 24px Arial';
        ctx.fillStyle = randomColor(50, 160);
        ctx.textBaseline = 'middle';
        ctx.save();
        ctx.translate(20 + i * 25, height / 2);
        ctx.rotate((randomNum(-30, 30) * Math.PI) / 180);
        ctx.fillText(text, -8, 2);
        ctx.restore();
      }
      realCaptchaCode.value = generatedCode;

      for (let i = 0; i <= 6; i++) {
        ctx.strokeStyle = randomColor(100, 200);
        ctx.beginPath();
        ctx.moveTo(randomNum(0, width), randomNum(0, height));
        ctx.lineTo(randomNum(0, width), randomNum(0, height));
        ctx.stroke();
      }
      for (let i = 0; i < 30; i++) {
        ctx.fillStyle = randomColor(0, 255);
        ctx.beginPath();
        ctx.arc(randomNum(0, width), randomNum(0, height), 1, 0, 2 * Math.PI);
        ctx.fill();
      }
    };

    const refreshCaptcha = () => {
      drawCaptcha();
      credentials.captcha = '';
    };

    // 🌟 新增：模式切換處理
    const toggleMode = () => {
      isLoginMode.value = !isLoginMode.value;
      errorMsg.value = '';
      successMsg.value = '';
      credentials.password = ''; // 切換模式時清空密碼
      refreshCaptcha();
    };

    // 🌟 修改：將 handleLogin 改為共用的 handleSubmit
    const handleSubmit = async () => {
      errorMsg.value = '';
      successMsg.value = '';
      isLoading.value = true;

      // 1. 優先檢查驗證碼
      if (credentials.captcha.toUpperCase() !== realCaptchaCode.value) {
        errorMsg.value = '驗證碼錯誤，請重新輸入。';
        isLoading.value = false;
        refreshCaptcha();
        return;
      }

      try {
        if (isLoginMode.value) {
          // =========================
          // A. 登入模式邏輯
          // =========================
          await api.login({
            username: credentials.username,
            password: credentials.password
          });
          
          // 🌟 登入成功後，不需要再存 localStorage 了！瀏覽器已自動將 auth_token 存入 HttpOnly Cookie。
          // 直接導向首頁，路由守衛 (index.ts) 會自動呼叫 /api/auth/me 去取得權限
          window.location.href = '/';
        } else {
          // =========================
          // B. 註冊模式邏輯
          // =========================
          await api.register({
            username: credentials.username,
            password: credentials.password
          });

          successMsg.value = '🎉 註冊成功！系統已預設您的權限為 Reviewer，請使用新密碼登入。';
          isLoginMode.value = true; // 註冊成功後自動切回登入模式
          credentials.password = ''; // 安全起見清空密碼
          refreshCaptcha();
        }
      } catch (error) {
        // 錯誤處理分流
        if (isLoginMode.value) {
          if (error.response?.status === 401) errorMsg.value = '帳號或密碼錯誤。';
          else if (error.response?.status === 403) errorMsg.value = '此帳號已被停用。';
          else errorMsg.value = '伺服器連線異常，請檢查網路狀態。';
        } else {
          if (error.response?.status === 400) errorMsg.value = error.response.data.message || '註冊失敗，請檢查輸入內容。';
          else errorMsg.value = '伺服器連線異常，請檢查網路狀態。';
        }
        refreshCaptcha();
        credentials.password = '';
      } finally {
        isLoading.value = false;
      }
    };

    const handleGoogleLogin = () => {
      alert('提示：Google 登入需要後端 API 支援。\n請先至 GCP 設定 OAuth Client ID。');
    };

    onMounted(() => {
        drawCaptcha();
      
    });

    return {
      credentials,
      isLoading,
      errorMsg,
      successMsg,
      isLoginMode,
      toggleMode,
      handleSubmit,
      handleGoogleLogin,
      captchaCanvas,
      refreshCaptcha
    };
  }
};