import { ref, computed } from 'vue';
import { authState } from '@/store/auth'; // 🌟 確保路徑指向你的 auth.ts

export default {
  name: 'Sidebar',
  setup() {
    const collapsed = ref(false);

    // 🌟 關鍵：computed 會追蹤 authState 的變化
    // 當 router 完成 API 請求並修改 authState 時，這裡會自動重新計算
    const filteredMenu = computed(() => {
      return authState.permissions
        .filter(p => p.isPublic || (p.actions && p.actions.includes('VIEW')))
        .map(p => ({
          name: p.routeName,
          icon: p.icon || '📌',
          text: p.title,
          // 優先使用後端給的 path，如果沒有則根據名稱生成
          path: p.path || (p.routeName === 'ClassifiedResults' ? '/' : `/${p.routeName.toLowerCase()}`)
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