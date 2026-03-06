/* src/App.js */
import { computed, ref, onMounted } from 'vue'; 
import { useRoute, useRouter } from 'vue-router';
import Sidebar from './components/Sidebar/index.vue';
import api from './api_clients/api'; 

export default {
  name: 'App',
  components: { Sidebar },

  setup() {
    const route = useRoute();
    const router = useRouter();

    const currentUser = ref({ name: '訪客 (Guest)', role: 'Guest' });

    // 🌟 只純粹拿資料，失敗了也不要重整網頁或重拿 Token
    const fetchUser = async () => {
      try {
        const res = await api.getMe();
        currentUser.value = {
          name: res.data.username,
          role: res.data.role
        };
      } catch (error) {
        // 安靜地失敗，因為 router.beforeEach 已經接管了驗證與跳轉
        console.log("App.js: 以訪客身分瀏覽");
      }
    };

    onMounted(() => {
      fetchUser();
    });

    const isLoggedIn = computed(() => {
      return currentUser.value.role !== 'Guest' && currentUser.value.name !== '';
    });

    const displayRole = computed(() => {
      if (!isLoggedIn.value) return 'Guest';
      const roleMap = { 'Admin': '管理員', 'Reviewer': '覆核員' };
      return roleMap[currentUser.value.role] || currentUser.value.role;
    });

    const currentPageTitle = computed(() => {
      // 🌟 對齊 DB 的標題
      const titles = {
        '/': '分類結果查看',
        '/hitl-dashboard': 'HITL 人工覆核中心',
        '/cold-start': '冷啟動建檔',
        '/monitor': '系統監控大盤',
        '/profile-manager': '追蹤人物管理',
        '/user-management': '帳號權限管理',
        '/login': 'System Access Control'
      };
      return titles[route.path] || 'IG AI Vision System';
    });

    const handleAuthAction = async () => {
      if (isLoggedIn.value) {
        if (confirm('確定要登出系統嗎？\nAre you sure you want to logout?')) {
          try { await api.logout(); } catch (e) { }
          window.location.href = '/'; // 🌟 登出後直接去登入頁
        }
      } else {
        router.push('/login');
      }
    };

    return {
      currentUser, isLoggedIn, displayRole, currentPageTitle, handleAuthAction
    };
  }
};