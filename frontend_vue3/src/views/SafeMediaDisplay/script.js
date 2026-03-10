// 檔案路徑: frontend_vue3/src/views/SafeMediaDisplay/script.js
import { computed, ref } from 'vue';

export default {
  props: {
    streamUrl: { type: String, required: true },
    fileName: { type: String, required: true }
  },

  setup(props) {
    // 綁定 Template 中的 video 標籤
    const videoRef = ref(null);

    const isVideo = computed(() => {
      return props.fileName?.toLowerCase().endsWith('.mp4');
    });

    // 🌟 滑鼠移入時播放
    const playVideo = () => {
      if (videoRef.value) {
        // 使用 catch 避免瀏覽器擋自動播放時在 Console 報錯
        videoRef.value.play().catch(() => {}); 
      }
    };

    // 🌟 滑鼠移出時暫停
    const pauseVideo = () => {
      if (videoRef.value) {
        videoRef.value.pause();
      }
    };

    return {
      isVideo,
      streamUrl: computed(() => props.streamUrl),
      videoRef,
      playVideo,
      pauseVideo
    };
  }
};