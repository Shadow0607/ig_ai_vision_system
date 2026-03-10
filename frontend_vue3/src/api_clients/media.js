// src/api_clients/media.js
import api from './api.js';

export const getMediaStream = async (mediaId) => {
  const response = await api.apiClient.get(`/MediaAssets/${mediaId}/stream`); 
  // 🌟 直接回傳物件中的 streamUrl 供前端 <video> 使用
  return response.data.streamUrl; 
};