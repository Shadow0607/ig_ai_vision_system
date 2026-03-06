import os
import io
import logging
import subprocess
import tempfile   # 🌟 新增：用於產生跨平台的安全暫存檔
import numpy as np

logger = logging.getLogger(__name__)

class MemoryMediaProcessor:
    @staticmethod
    def merge_cover_to_video_bytes(video_bytes: bytes, image_bytes: bytes) -> bytes:
        """
        🌟 跨平台相容版合併：封面圖極速暫存，影片全記憶體處理
        """
        temp_img_path = ""
        try:
            # 1. 將幾 KB 的封面圖寫入系統暫存檔 (跨平台絕對安全)
            with tempfile.NamedTemporaryFile(suffix='.jpg', delete=False) as temp_img:
                temp_img.write(image_bytes)
                temp_img_path = temp_img.name

            # 2. 建立指令：影片吃記憶體 (pipe:0)，圖片吃暫存檔
            cmd = [
                'ffmpeg', '-y',
                '-i', 'pipe:0',              # 影片輸入 (從 stdin 記憶體)
                '-i', temp_img_path,         # 圖片輸入 (從剛剛的暫存檔)
                '-map', '0:v', 
                '-map', '0:a?', 
                '-map', '1:0',
                '-c', 'copy',                # 快速複製，不轉碼
                '-disposition:v:1', 'attached_pic',
                '-id3v2_version', '3',
                '-f', 'mp4',                 
                '-movflags', 'frag_keyframe+empty_moov', 
                'pipe:1'                     # 輸出到 stdout 記憶體
            ]

            process = subprocess.Popen(
                cmd, 
                stdin=subprocess.PIPE, 
                stdout=subprocess.PIPE, 
                stderr=subprocess.PIPE
            )

            # 將影片 bytes 灌入 stdin，並接收合併後的結果
            out, err = process.communicate(input=video_bytes)
            
            if process.returncode != 0:
                logger.error(f"❌ 記憶體合併失敗: {err.decode('utf-8', 'ignore')}")
                return video_bytes
            return out
            
        except Exception as e:
            logger.error(f"❌ 合併發生系統錯誤: {e}")
            return video_bytes
            
        finally:
            # 🌟 3. 無論成功或失敗，立刻把圖片暫存檔刪除，維持無狀態精神
            if temp_img_path and os.path.exists(temp_img_path):
                try:
                    os.remove(temp_img_path)
                except:
                    pass

    @staticmethod
    def is_video_static_bytes(video_bytes: bytes, threshold: int = 10) -> bool:
        """
        🌟 無狀態偵測：判斷影片是否為靜態 (不使用 OpenCV 路徑)
        """
        try:
            frame1 = MemoryMediaProcessor._get_frame_at_time(video_bytes, "00:00:01")
            frame2 = MemoryMediaProcessor._get_frame_at_time(video_bytes, "00:00:03")
            
            if frame1 is None or frame2 is None: return False

            score = np.mean((frame1.astype(np.float32) - frame2.astype(np.float32)) ** 2)
            return score < threshold
        except Exception as e:
            logger.error(f"靜態影片檢查失敗: {e}")
            return False

    @staticmethod
    def _get_frame_at_time(video_bytes: bytes, timestamp: str):
        """私有輔助：從 bytes 提取特定時間點的 raw 影像"""
        cmd = [
            'ffmpeg', '-i', 'pipe:0',
            '-ss', timestamp,
            '-vframes', '1',
            '-f', 'rawvideo',
            '-pix_fmt', 'gray', 
            '-s', '128x128',    
            'pipe:1'
        ]
        process = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        out, _ = process.communicate(input=video_bytes)
        if out:
            return np.frombuffer(out, dtype=np.uint8)
        return None