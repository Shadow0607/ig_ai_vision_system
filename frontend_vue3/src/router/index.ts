// @ts-nocheck
import { createRouter, createWebHistory } from 'vue-router'
import api from '../api_clients/api'
import { authState } from '../store/auth'; // 🌟 引入響應式狀態
import { usePermissions } from '@/composables/usePermissions';
const pages = import.meta.glob('../views/*/index.vue')

const routes = Object.keys(pages).map((filePath) => {
  const folderName = filePath.match(/\/views\/(.*)\/index\.vue$/)[1];
  if (folderName === 'NotFound') return null;

  let routePath = `/${folderName.toLowerCase()}`;

  // 依照定義綁定路徑
  if (folderName === 'ClassifiedResults') routePath = '/';
  else if (folderName === 'HitlDashboard') routePath = '/hitl-dashboard';
  else if (folderName === 'SystemMonitor') routePath = '/monitor';
  else if (folderName === 'ColdStartSetup') routePath = '/cold-start';
  else if (folderName === 'ProfileManager') routePath = '/profile-manager';
  else if (folderName === 'UserManagement') routePath = '/user-management';

  return {
    path: routePath,
    name: folderName,
    component: pages[filePath]
  }
}).filter(Boolean) as any[]

routes.push({ path: '/login', name: 'Login', component: () => import('../views/Login/index.vue'), meta: { title: '系統存取控制' } })
routes.push({ path: '/:pathMatch(.*)*', name: 'NotFound', component: () => import('../views/NotFound/index.vue'), meta: { title: '404 - 訊號遺失' } })

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes
})

// 🌟 1. 這裡把 next 拿掉了
router.beforeEach(async (to, from) => {
  // 🌟 2. 修正：讓去 Login 和 NotFound 的人正常通行
  if (to.name === "Login" || to.name === "NotFound") return true; 

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
        // 🌟 3. 修正：移除 next()，直接 return 路由物件
        return { name: "Login" };
      }
    }
  }

  const targetRoute = authState.permissions.find((p) => p.routeName === to.name);
  
  if (targetRoute) {
    if (targetRoute.isPublic || (targetRoute.actions && targetRoute.actions.includes("VIEW"))) {
      document.title = `${targetRoute.title} | IG AI System`;
      return true;
    } else {
      return { name: "NotFound" };
    }
  }

  // 🌟 4. 新增：最後的防呆機制。如果找不到對應的路由權限，一律去 NotFound
  return { name: "NotFound" };
});
export default router;