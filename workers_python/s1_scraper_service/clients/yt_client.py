import os
import tempfile
import logging
import yt_dlp

logger = logging.getLogger(__name__)

class YTClient:
    def __init__(self):
        # 取得清單用的輕量化設定 (不下載)
        self.base_opts = {
            'quiet': True,
            'extract_flat': True,
            'force_generic_extractor': False,
        }

    def get_channel_videos(self, channel_url: str) -> list:
        """秒速獲取頻道所有影片/Shorts 清單"""
        results = []
        try:
            with yt_dlp.YoutubeDL(self.base_opts) as ydl:
                info = ydl.extract_info(channel_url, download=False)
                if 'entries' in info:
                    for entry in info['entries']:
                        if entry:
                            results.append({
                                'id': entry.get('id'),
                                'url': entry.get('url'),
                                'title': entry.get('title'),
                                'is_shorts': '/shorts/' in channel_url
                            })
            return results
        except Exception as e:
            logger.error(f"❌ 獲取 YouTube 清單失敗 ({channel_url}): {e}")
            return []

    def download_video_to_temp(self, video_url: str):
        """下載影片與縮圖到暫存區，回傳路徑供讀取"""
        temp_dir = tempfile.mkdtemp()
        
        ydl_opts = {
            'format': 'bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best',
            'outtmpl': f'{temp_dir}/%(id)s.%(ext)s',
            'writethumbnail': True,
            'quiet': True,
            'no_warnings': True,
        }

        try:
            with yt_dlp.YoutubeDL(ydl_opts) as ydl:
                info = ydl.extract_info(video_url, download=True)
                
                # 尋找下載好的 MP4 與 JPG/WEBP 縮圖
                downloaded_files = os.listdir(temp_dir)
                video_path = next((os.path.join(temp_dir, f) for f in downloaded_files if f.endswith('.mp4')), None)
                thumb_path = next((os.path.join(temp_dir, f) for f in downloaded_files if not f.endswith('.mp4')), None)
                
                return video_path, thumb_path, temp_dir
        except Exception as e:
            logger.error(f"❌ 下載 YouTube 影片失敗 ({video_url}): {e}")
            return None, None, temp_dir