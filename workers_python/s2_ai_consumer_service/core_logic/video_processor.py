import os
import cv2
import uuid
import logging
import tempfile
import subprocess
from .decision_engine import DecisionEngine

logger = logging.getLogger(__name__)

def convert_to_h264_if_needed(video_path):
    """
    測試能否讀取，若為 AV1 導致 OpenCV 崩潰，則強制轉碼為 H.264
    """
    cap = cv2.VideoCapture(video_path)
    ret, frame = cap.read()
    cap.release()
    
    if ret:
        return video_path # 讀取正常，直接回傳原路徑
        
    logger.warning(f"⚠️ OpenCV 讀取失敗 (可能為 AV1 編碼)，啟動 FFmpeg 強制轉碼: {video_path}")
    temp_h264_path = video_path.replace(".mp4", "_h264.mp4")
    
    # 呼叫系統 ffmpeg 轉成 h264，並使用軟體解碼
    command = [
        "ffmpeg", "-y", "-i", video_path, 
        "-c:v", "libx264", "-preset", "ultrafast", 
        "-c:a", "copy", temp_h264_path
    ]
    
    try:
        subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
        return temp_h264_path
    except Exception as e:
        logger.error(f"❌ FFmpeg 轉碼失敗: {e}")
        return video_path # 轉碼失敗還是回傳原檔，聽天由命

class VideoProcessor:
    def __init__(self, extractor, router, bank_manager):
        self.extractor = extractor
        self.router = router
        self.qdrant = bank_manager.qdrant

    def process_long_video(self, media_id: int, system_name: str, s3_key: str, db_threshold: float, sample_rate_sec: float = 2.0):
        """
        處理長影片核心邏輯：
        下載暫存 -> 每 N 秒抽一幀 -> AI 辨識 -> 命中則獨立上傳截圖與寫入 DB -> 搬移原始影片 (不刪除)
        """
        logger.info(f"🎬 [Video Processor] 開始處理長影片: {s3_key}")
        
        # 1. 建立本機暫存檔 (避免 2GB 影片塞爆記憶體)
        _, ext = os.path.splitext(s3_key)
        with tempfile.NamedTemporaryFile(suffix=ext, delete=False) as tmp_file:
            local_video_path = tmp_file.name

        # 準備另一個變數用來存「可能轉碼過」的影片路徑，確保最後都能刪除乾淨
        safe_video_path = local_video_path

        try:
            # 2. 從 S3 下載完整影片至本機暫存
            if not self.router.download_file(s3_key, local_video_path):
                logger.error(f"❌ 影片下載至本機失敗: {s3_key}")
                return False

            # 🌟 核心修正：加入轉碼防禦機制，避免 AV1 解碼失敗
            safe_video_path = convert_to_h264_if_needed(local_video_path)

            cap = cv2.VideoCapture(safe_video_path)
            if not cap.isOpened():
                logger.error(f"❌ 無法解析影片檔: {s3_key}")
                return False

            fps = cap.get(cv2.CAP_PROP_FPS)
            if fps <= 0: fps = 30
            total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
            
            # 設定抽幀間距 (例如：每 2 秒抽 1 幀，大幅節省算力)
            frame_interval = int(fps * sample_rate_sec)
            
            match_count = 0
            current_frame = 0

            logger.info(f"🎞️ 影片資訊: 總幀數 {total_frames}, FPS {fps:.1f}, 預計每 {sample_rate_sec} 秒抽幀檢查")

            # 3. 迴圈抽幀與 AI 檢測
            while current_frame < total_frames:
                # 瞬間跳轉到指定影格 (效能極高)
                cap.set(cv2.CAP_PROP_POS_FRAMES, current_frame)
                success, frame = cap.read()
                if not success:
                    break

                # AI 提取人臉特徵
                target_embedding, face_detected = self.extractor.extract_target_embedding(frame)
                
                if face_detected and target_embedding:
                    # 呼叫 Qdrant 決策引擎
                    result, score, target_folder = DecisionEngine.compare_face_logic(
                        target_embedding, system_name, self.qdrant, db_threshold
                    )

                    # 如果高度相似，則將該影格獨立裁切保存
                    if result == "MATCH_VSTACK":
                        match_count += 1
                        self._save_matched_frame(frame, system_name, s3_key, current_frame, fps, media_id, score)

                current_frame += frame_interval

            cap.release()
            logger.info(f"✅ 影片處理完畢！共為 {system_name} 萃取出 {match_count} 張目標臉部截圖。")

            # 4. 原始影片處置 (保留原始檔案，僅搬移分類)
            final_status = "OUTPUT" if match_count > 0 else "NOFACE"
            final_score = 1.0 if match_count > 0 else 0.0
            dest_folder = "pos" if match_count > 0 else "NOFACE"

            new_video_key = self.router.move_file_safe(s3_key, dest_folder)
            if new_video_key:
                self.router.update_media_asset_path(media_id, new_video_key)
                self.router.update_db_log(media_id, final_status, match_count > 0, final_score)

            return True

        except Exception as e:
            logger.error(f"❌ 影片處理期間發生意外錯誤: {e}")
            return False
        finally:
            # 5. 無論成功失敗，務必刪除本機暫存的 2GB 大檔，釋放磁碟空間
            if os.path.exists(local_video_path):
                os.remove(local_video_path)
            # 如果有產生轉碼後的 H.264 暫存檔，也要一併刪除
            if safe_video_path != local_video_path and os.path.exists(safe_video_path):
                os.remove(safe_video_path)

    def _save_matched_frame(self, frame_img, system_name, original_s3_key, frame_idx, fps, parent_media_id, score):
        """將符合條件的單一影格，獨立轉存為全新圖片並上傳 S3 與 DB"""
        sec = int(frame_idx / fps)
        base_name = os.path.basename(original_s3_key).rsplit('.', 1)[0]
        # 命名規則：原檔名_frame_秒數s_亂數.jpg
        new_filename = f"{base_name}_frame_{sec}s_{uuid.uuid4().hex[:6]}.jpg"
        target_key = f"{system_name}/pos/{new_filename}"

        # 圖片編碼
        success, buffer = cv2.imencode(".jpg", frame_img)
        if not success: return

        try:
            # 1. 獨立上傳至 S3
            self.router.s3_client.put_object(
                Bucket=self.router.bucket_name,
                Key=target_key,
                Body=buffer.tobytes(),
                ContentType='image/jpeg'
            )
            
            # 2. 將這張截圖作為「全新的資產」寫入 MySQL 資料庫
            conn = self.router._get_connection()
            with conn.cursor() as cursor:  # 🌟 移除了 dictionary=True
                # 依序撈出 original_shortcode (索引 0) 與 original_username (索引 1)
                cursor.execute("SELECT original_shortcode, original_username FROM media_assets WHERE id = %s", (parent_media_id,))
                # 🌟 如果沒撈到資料，預設給一個含有兩個 None 的 Tuple
                parent_info = cursor.fetchone() or (None, None) 
                
                sql = """
                    INSERT INTO media_assets 
                    (system_name, file_name, file_path, file_size_bytes, media_type, original_shortcode, original_username) 
                    VALUES (%s, %s, %s, %s, %s, %s, %s)
                """
                cursor.execute(sql, (
                    system_name, new_filename, target_key, len(buffer.tobytes()), 
                    'IMAGE', parent_info[0], parent_info[1] # 🌟 改用索引 [0] 和 [1] 取值
                ))
                new_media_id = cursor.lastrowid
                conn.commit()
                
            # ... (下半部維持不變) ...
            
            # 3. 更新這張新截圖的 AI 判定日誌
            self.router.update_db_log(new_media_id, "OUTPUT", True, score)
            logger.info(f"📸 截圖擷取成功並上傳: {new_filename} (分數: {score:.2f})")
            
        except Exception as e:
            logger.error(f"❌ 截圖獨立保存失敗: {e}")
        finally:
            if 'conn' in locals() and conn.open:
                conn.close()