import os
import time
import cv2
import numpy as np
import logging
import subprocess  # 🌟 改用內建的 subprocess

logger = logging.getLogger(__name__)

class MediaProcessor:
    @staticmethod
    def merge_thumbnail_to_video(video_file: str, image_file: str) -> bool:
        """將封面圖嵌入 MP4 影片中"""
        temp_video = f"{video_file}_temp.mp4"
        time.sleep(1.0) # 緩衝

        if os.path.exists(video_file) and os.path.exists(image_file):
            try:
                # 🌟 使用原生指令陣列，徹底避開 ffmpeg-python 的語法地雷
                cmd = [
                    'ffmpeg', '-y',          # -y 代表自動覆寫檔案
                    '-i', video_file,        # 輸入檔案 0 (影片)
                    '-i', image_file,        # 輸入檔案 1 (圖片)
                    '-map', '0:v',           # 擷取影片的影像軌
                    '-map', '0:a?',          # 擷取影片的音軌 (加問號代表如果沒聲音也不要報錯)
                    '-map', '1:0',           # 擷取圖片作為封面
                    '-c', 'copy',            # 複製編碼 (不重新轉檔，速度極快)
                    '-disposition:v:1', 'attached_pic', # 標記圖片為封面圖
                    '-id3v2_version', '3',   # 強制寫入 ID3 標籤
                    temp_video               # 輸出檔案
                ]
                
                # 執行指令並捕捉輸出
                result = subprocess.run(
                    cmd, 
                    capture_output=True, 
                    text=True, 
                    encoding='utf-8', 
                    errors='ignore'
                )
                
                # 如果執行失敗 (returncode 不為 0)
                if result.returncode != 0:
                    logger.error(f"合併影音失敗 (FFmpeg 底層錯誤):\n{result.stderr}")
                    return False
                    
                time.sleep(0.5)
                if os.path.exists(temp_video): 
                    os.replace(temp_video, video_file)
                return True
                
            except Exception as e:
                logger.error(f"合併影音發生系統錯誤: {e}")
                return False
        return False

    @staticmethod
    def is_video_static(video_path: str, threshold: int = 10) -> bool:
        """比對影片影格，判斷是否為靜態假影片"""
        try:
            cap = cv2.VideoCapture(video_path)
            if not cap.isOpened(): return False
            total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
            if total_frames < 2: return False 

            frame1_idx = total_frames // 2
            cap.set(cv2.CAP_PROP_POS_FRAMES, frame1_idx)
            ret, frame = cap.read()

            frame2_idx = min(frame1_idx + 10, total_frames - 1)
            if frame1_idx == frame2_idx:
                cap.release()
                return False

            cap.set(cv2.CAP_PROP_POS_FRAMES, frame2_idx)
            ret2, frame2 = cap.read()
            cap.release()
            
            if not ret or not ret2: return False
            gray1 = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
            gray2 = cv2.cvtColor(frame2, cv2.COLOR_BGR2GRAY)
            score = np.mean((gray1 - gray2) ** 2)
            return score < threshold
        except Exception as e:
            logger.error(f"靜態影片檢查失敗: {e}")
            return False