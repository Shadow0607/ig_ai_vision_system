// @ts-nocheck
import { createRouter, createWebHistory } from 'vue-router'
import api from '../api_clients/api'
import { authState } from '../store/auth'
import { RouteNames } from '@/constants/routeNames' // 🌟 引入 Enum 確保名稱對齊

// 自動導入 views 資料夾下的 index.vue
const pages = import.meta.glob('../views/*/index.vue')

// 🌟 動態路徑映射表：將資料夾名稱對應到路徑 (key 必須符合 RouteNames)
const pathMap: Record<string, string> = {
  [RouteNames.ClassifiedResults]: '/',
  [RouteNames.HitlDashboard]: '/hitl-dashboard',
  [RouteNames.SystemMonitor]: '/monitor',
  [RouteNames.ColdStartSetup]: '/cold-start',
  [RouteNames.ProfileManager]: '/profile-manager',
  [RouteNames.UserManagement]: '/user-management',
}

// 建立動態路由清單
const routes = Object.keys(pages).map((filePath) => {
  const folderName = filePath.match(/\/views\/(.*)\/index\.vue$/)[1];
  
  // 排除不需要動態生成的頁面
  if (folderName === 'NotFound' || folderName === 'Login') return null;

  // 🌟 確保 folderName 符合 RouteNames 定義，並取得對應路徑
  const routeName = folderName as RouteNames;
  const routePath = pathMap[routeName] || `/${folderName.toLowerCase()}`;

  return {
    path: routePath,
    name: routeName, // 🌟 name 現在嚴格等於 RouteNames Enum
    component: pages[filePath]
  }
}).filter(Boolean) as any[]

// 插入固定路由
routes.push({ 
  path: '/login', 
  name: RouteNames.Login, 
  component: () => import('../views/Login/index.vue'), 
  meta: { title: '系統存取控制' } 
})

routes.push({ 
  path: '/:pathMatch(.*)*', 
  name: RouteNames.NotFound, 
  component: () => import('../views/NotFound/index.vue'), 
  meta: { title: '404 - 訊號遺失' } 
})

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes
})

// 🌟 路由守衛：權限驗證邏輯
router.beforeEach(async (to, from) => {
  // 1. 公開頁面 (Login / 404) 直接放行
  if (to.name === RouteNames.Login || to.name === RouteNames.NotFound) return true; 

  // 2. 確保權限狀態已載入
  if (!authState.isLoaded) {
    try {
      const res = await api.getMe();
      authState.permissions = res.data.permissions;
      authState.username = res.data.username;
      authState.role = res.data.role;
      authState.isLoaded = true;
    } catch (error) {
      try {
        await api.getGuestToken();
        const guestRes = await api.getMe();
        authState.permissions = guestRes.data.permissions;
        authState.isLoaded = true;
      } catch (guestErr) {
        return { name: RouteNames.Login };
      }
    }
  }

  // 3. 權限比對邏輯 (使用 routeName 與 to.name 進行強對齊)
  // 此處的 p.routeName 來自後端資料庫 system_routes.route_name
  const targetRoute = authState.permissions.find((p) => p.routeName === to.name);
  
  if (targetRoute) {
    // 檢查是否有 VIEW 權限或是否為公開路由
    const hasViewPermission = targetRoute.actions && targetRoute.actions.includes("VIEW");
    
    if (targetRoute.isPublic || hasViewPermission) {
      document.title = `${targetRoute.title} | IG AI System`;
      return true;
    }
  }

  // 4. 防呆機制：若無權限或找不到路由，一律導向 404
  return { name: RouteNames.NotFound };
});

export default router;