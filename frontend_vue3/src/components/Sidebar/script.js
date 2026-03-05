import { ref, computed, watchEffect } from 'vue';
import { useRoute } from 'vue-router'; // 🌟 引入 useRoute

export default {
  name: 'Sidebar',
  setup() {
    const route = useRoute();
    const collapsed = ref(false);
    
    // 🌟 將權限轉為響應式變數
    const permissions = ref(window.__USER_PERMISSIONS__ || []);

    // 🌟 監聽路由變化：每次網址改變時，強制同步最新的權限狀態
    watchEffect(() => {
      const trigger = route.path; // 觸發依賴
      if (window.__USER_PERMISSIONS__) {
        permissions.value = window.__USER_PERMISSIONS__;
      }
    });
    
    const filteredMenu = computed(() => {
      // 改從響應式的 permissions 讀取
      return permissions.value
        .filter(p => p.canView)
        .map(p => ({
          name: p.routeName,
          icon: p.icon || p.Icon || '📌',
          text: p.title || p.Title || p.routeName,
          path: p.routeName === 'ClassifiedResults' ? '/' : (p.path || p.Path || `/${p.routeName.toLowerCase()}`)
        }));
    });

    const handleClick = () => {
      collapsed.value = !collapsed.value; 
    };

    return { 
      collapsed, 
      filteredMenu, 
      handleClick 
    };
  }
};