// src/composables/usePermissions.js
import { computed } from 'vue';
import { authState } from '@/store/auth';

export function usePermissions(routeName: any) {
    const myPerms = computed(() => {
        const permissions = authState.permissions || [];
        const p = permissions.find(item => item.routeName === routeName);
        
        // 🌟 獲取該路由允許的動作代碼陣列 (需確保後端登入 API 有回傳 actions 陣列)
        const allowedActions = p?.actions || [];

        return {
            // 提供動態檢查函數，取代寫死的 canView, canCreate
            hasAction: (actionCode: string) => allowedActions.includes(actionCode),
            isLoaded: authState.isLoaded
        };
    });
    return { myPerms };
}