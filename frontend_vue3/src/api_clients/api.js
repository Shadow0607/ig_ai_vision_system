// frontend_vue3/api_clients/api.js
import axios from 'axios';
const apiClient = axios.create({
  baseURL: 'http://localhost:5000/api', 
  headers: {
    'Content-Type': 'application/json',
  },
  timeout: 10000,
  withCredentials: true
});
apiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    return Promise.reject(error);
  }
);

export default {
  apiClient,
  getMe() { return apiClient.get('/auth/me'); },
  logout() { return apiClient.post('/auth/logout'); },
  login(credentials) { return apiClient.post('/auth/login', credentials); },
  register(credentials) { return apiClient.post('/auth/register', credentials); }, // 🆕 註冊帳號
  getAllUsers() { return apiClient.get('/auth/users'); }, // 🆕 獲取清單
  updateUser(id, data) { return apiClient.put(`/auth/users/${id}`, data); }, 
  deleteUser(id) { return apiClient.delete(`/auth/users/${id}`); }, // 🆕 刪除使用者
  getHitlPendingImages() { return apiClient.get('/hitl/pending'); },
  approveHitlBatch(ids) { return apiClient.post('/hitl/batch-approve', { ids }); },
  rejectHitlImage(imageId) { return apiClient.post('/hitl/reject', { imageId }); },
  approveHitlImage(imageId, targetPerson) { return apiClient.post('/hitl/approve', { imageId, targetPerson }); },
  getColdStartImages() { return apiClient.get('/coldstart/pending'); },
  setBaselineImage(imageId, targetPerson) { return apiClient.post('/coldstart/set-baseline', { imageId, targetPerson }); },
  getPendingColdStarts() { return apiClient.get('/ColdStart/pending'); },
  confirmColdStart(payload) { return apiClient.post('/ColdStart/confirm', payload); },
  rejectColdStart(payload) { return apiClient.post('/ColdStart/reject', payload); },
  uploadColdStartMedia(systemName, formData) { 
    return apiClient.post(`/ColdStart/${systemName}/upload`, formData, {
      headers: { 'Content-Type': 'multipart/form-data' } // 必須設定為 multipart 才能傳檔案
    }); 
  },
  getSystemAlerts() { return apiClient.get('/monitor/alerts'); },
  getAiStatistics() { return apiClient.get('/monitor/statistics'); },
  getAllPersons() { return apiClient.get('/config/persons'); },
  createPerson(personData) { return apiClient.post('/config/persons', personData); },
  updatePerson(id, updateData) { return apiClient.put(`/config/persons/${id}`, updateData); },
  deletePerson(id) { return apiClient.delete(`/config/persons/${id}`); },
  addAccount(personId, accountData) { return apiClient.post(`/config/persons/${personId}/accounts`, accountData); },
  getPlatforms() { return apiClient.get('/config/platforms'); },
  getAccountTypes() { return apiClient.get('/config/account-types'); },
  updateAccount(id, data) { return apiClient.put(`/config/accounts/${id}`, data); },
  deleteAccount(accountId) { return apiClient.delete(`/config/accounts/${accountId}`); },
  
  uploadManualPhotos: (systemName, formData) => {
    return apiClient.post(`/Config/persons/${systemName}/upload`, formData, {
      headers: { 'Content-Type': 'multipart/form-data' } 
    });
  },
  deleteManualPhoto(mediaId) { 
    return apiClient.delete(`/config/persons/media/${mediaId}`); 
  },
  getAllUsers() { return apiClient.get('/auth/users'); },        // 獲取使用者清單
  updateUser(id, data) { return apiClient.put(`/auth/users/${id}`, data); }, // 更新使用者角色與狀態
  getGuestToken() { return apiClient.get('/auth/guest-token'); },
  deleteUser(id) { return apiClient.delete(`/auth/users/${id}`); },
  resetUserPassword(id, newPassword) { return apiClient.put(`/auth/users/${id}/reset-password`, { newPassword }); },
  getClassifiedMedia(status, systemName = '', page = 1, pageSize = 50) { 
    let url = `/MediaAssets/classified?status=${status}&page=${page}&pageSize=${pageSize}`;
    if (systemName) url += `&systemName=${systemName}`;
    return apiClient.get(url); 
  },
  reclassifyMedia(payload) { return apiClient.put('/MediaAssets/reclassify', payload); },
  batchReclassifyMedia(payload) { return apiClient.put('/MediaAssets/batch-reclassify', payload); },
  getSystemRoutes() { return apiClient.get('/auth/routes'); },
  getRolePermissions(roleId) { return apiClient.get(`/auth/roles/${roleId}/permissions`); },
  updateRolePermissions(roleId, permissions) { return apiClient.put(`/auth/roles/${roleId}/permissions`, permissions); },
  getRoles() { return apiClient.get('/auth/roles'); },
  createRole(data) { return apiClient.post('/auth/roles', data); },
  updateRole(id, data) { return apiClient.put(`/auth/roles/${id}`, data); },
  deleteRole(id) { return apiClient.delete(`/auth/roles/${id}`); },
  getSysActions: () => apiClient.get('/SysActions'),
  getPendingReposts() {
    return apiClient.get('/repost-review/pending');
  },
  decideRepost(payload) {
    return apiClient.post('/repost-review/decide', payload);
  }
};