// @ts-nocheck
import { createRouter, createWebHistory } from 'vue-router'
import api from '../api_clients/api' 

let memoryPermissions: any[] | null = null;

const pages = import.meta.glob('../views/*/index.vue')

const routes = Object.keys(pages).map((filePath) => {
  const folderName = filePath.match(/\/views\/(.*)\/index\.vue$/)[1];
  if (folderName === 'NotFound') return null;

  let routePath = `/${folderName.toLowerCase()}`;

  // 🌟 核心修復：完全依照 DB 截圖的 route_name 與 path 進行綁定！
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

routes.push({ path: '/login', name: 'Login', component: () => import('../views/Login/index.vue'), meta: { title: '系統存取控制' }})
routes.push({ path: '/:pathMatch(.*)*', name: 'NotFound', component: () => import('../views/NotFound/index.vue'), meta: { title: '404 - 訊號遺失' }})

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes
})

// 🌟 路由守衛 (集中處理權限，解決 401 衝突)
router.beforeEach(async (to, from, next) => {
  if (to.name === 'Login' || to.name === 'NotFound') return next();

  if (!memoryPermissions) {
    try {
      // 嘗試讀取 Cookie 憑證
      const res = await api.getMe();
      memoryPermissions = res.data.permissions; 
      window.__USER_PERMISSIONS__ = memoryPermissions;
    } catch (error) {
      // 憑證無效或沒登入，拿訪客 Token
      try {
         await api.getGuestToken();
         const guestRes = await api.getMe();
         memoryPermissions = guestRes.data.permissions;
         window.__USER_PERMISSIONS__ = memoryPermissions;
      } catch (guestErr) {
         return next({ name: 'Login' }); 
      }
    }
  }

  const targetRoute = memoryPermissions.find((p: any) => p.routeName === to.name);

  if (targetRoute && (targetRoute.isPublic || targetRoute.canView)) {
    document.title = `${targetRoute.title} | IG AI System`; 
    next();
  } else {
    // 💡 如果被丟來這裡，代表該角色 (例如訪客) 沒有這個頁面的 canView 權限！
    console.warn(`權限阻擋：沒有存取 ${to.name} 的權限`);
    
    // 如果是訪客被擋在首頁，引導去登入，而不是丟去 404
    if (!window.__USER_PERMISSIONS__?.find(p => p.canView)) {
        next({ name: 'Login' });
    } else {
        next({ name: 'NotFound' }); 
    }
  }
});

export default router