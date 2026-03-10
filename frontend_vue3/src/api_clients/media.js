// src/api_clients/media.js
import api from './api.js'; // 必須引入原本寫好的 api.js

export const getMediaStream = async (mediaId) => {
  // 使用 api.apiClient 以確保發送時有帶 Token
  const response = await api.apiClient.get(`/MediaAssets/${mediaId}/stream`); 
  return response.data; 
};