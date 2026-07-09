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
        處理長影片核心邏輯 (輕量檢查版)：
        僅確認影片中是否有目標人物，不移動原始檔案。
        """
        logger.info(f"🎬 [Video Processor] 開始分析長影片內容: {s3_key}")
        
        # 1. 建立本機暫存檔 (供 OpenCV 分析使用)
        _, ext = os.path.splitext(s3_key)
        with tempfile.NamedTemporaryFile(suffix=ext, delete=False) as tmp_file:
            local_video_path = tmp_file.name

        safe_video_path = local_video_path

        try:
            # 2. 下載影片至暫存
            if not self.router.download_file(s3_key, local_video_path):
                logger.error(f"❌ 影片下載失敗: {s3_key}")
                return False

            # 🌟 AV1 防禦機制：確保 OpenCV 讀得到畫面
            safe_video_path = convert_to_h264_if_needed(local_video_path)

            cap = cv2.VideoCapture(safe_video_path)
            if not cap.isOpened():
                logger.error(f"❌ 無法解析影片檔: {s3_key}")
                return False

            fps = cap.get(cv2.CAP_PROP_FPS)
            if fps <= 0: fps = 30
            total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
            frame_interval = int(fps * sample_rate_sec)
            
            match_count = 0
            current_frame = 0

            logger.info(f"🎞️ 影片資訊: {total_frames} 幀, 每 {sample_rate_sec} 秒抽檢一次")

            # 3. 抽幀辨識迴圈
            while current_frame < total_frames:
                cap.set(cv2.CAP_PROP_POS_FRAMES, current_frame)
                success, frame = cap.read()
                if not success: break

                # AI 人臉萃取
                target_embedding, face_detected = self.extractor.extract_target_embedding(frame)
                
                if face_detected and target_embedding:
                    # Qdrant 向量比對
                    result, score, _ = DecisionEngine.compare_face_logic(
                        target_embedding, system_name, self.qdrant, db_threshold
                    )

                    # 若命中，存出截圖並計數
                    if result == "MATCH_VSTACK":
                        match_count += 1
                        # 依然保存截圖資產，作為該影片含有目標人物的證據
                        self._save_matched_frame(frame, system_name, s3_key, current_frame, fps, media_id, score)

                current_frame += frame_interval

            cap.release()
            
            # ==========================================
            # 🌟 4. 關鍵決策邏輯 (不移動檔案版)
            # ==========================================
            # 有命中 -> 標記 OUTPUT (本人)
            # 沒命中 -> 標記 PENDING (待審核) 供人工二次確認
            final_status = "OUTPUT" if match_count > 0 else "PENDING"
            final_score = 1.0 if match_count > 0 else 0.0

            logger.info(f"🏁 分析結束: {system_name} 匹配數 {match_count}。結果: {final_status}")

            # 🌟 只更新資料庫 Log 狀態，不更動 FilePath (檔案留在原處)
            self.router.update_db_log(media_id, final_status, match_count > 0, final_score)

            return True

        except Exception as e:
            logger.error(f"❌ 長影片處理異常: {e}")
            return False
        finally:
            # 5. 清理本機暫存空間 (避免磁碟爆滿)
            if os.path.exists(local_video_path):
                os.remove(local_video_path)
            if safe_video_path != local_video_path and os.path.exists(safe_video_path):
                os.remove(safe_video_path)

    def _save_matched_frame(self, frame_img, system_name, original_s3_key, frame_idx, fps, parent_media_id, score):
        """完全動態化的證據截圖存檔"""
        from utils.hash_helper import HashHelper
        
        # 1. 取得必要的動態 ID (拒絕寫死)
        # 從 SysStatusManager 根據 Code 獲取真正對應的資料庫 ID
        image_type_id = self.router.status_manager.get_id("MEDIA_TYPE", "IMAGE")
        downloaded_id = self.router.status_manager.get_id("DOWNLOAD_STATUS", "DOWNLOADED")

        sec = int(frame_idx / fps)
        base_name = os.path.basename(original_s3_key).rsplit('.', 1)[0]
        new_filename = f"{base_name}_frame_{sec}s_{uuid.uuid4().hex[:6]}.jpg"
        target_key = f"{system_name}/pos/{new_filename}"

        success, buffer = cv2.imencode(".jpg", frame_img)
        if not success: return
        
        img_bytes = buffer.tobytes()
        p_hash, md5_hash = HashHelper.get_dual_fingerprints(img_bytes, new_filename)
        # 🌟 整合數位指紋，確保截圖也能去重

        try:
            # 2. 上傳至 S3
            self.router.s3_client.put_object(
                Bucket=self.router.bucket_name,
                Key=target_key,
                Body=img_bytes,
                ContentType='image/jpeg'
            )
            
            # 3. 寫入資料庫 (完全繼承父系屬性與動態 ID)
            conn = self.router._get_connection()
            try:
                with conn.cursor() as cursor:
                    # 繼承父影片的關鍵資訊，包含 person_id 與 source_type_id
                    cursor.execute("""
                        SELECT original_shortcode, original_username, person_id, source_type_id 
                        FROM media_assets WHERE id = %s
                    """, (parent_media_id,))
                    p_info = cursor.fetchone() or (None, None, None, None)
                    
                    sql = """
                        INSERT INTO media_assets 
                        (person_id, system_name, file_name, file_path, file_size_bytes, 
                        media_type_id, source_type_id, download_status_id,
                        original_shortcode, original_username, image_hash, file_hash) 
                        VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
                    """
                    cursor.execute(sql, (
                        p_info[2], system_name, new_filename, target_key, len(img_bytes), 
                        image_type_id, p_info[3], downloaded_id, 
                        p_info[0], p_info[1], p_hash, md5_hash # 🌟 填入雙指紋
                    ))
                    new_media_id = cursor.lastrowid
                    conn.commit()
                    
                # 4. 更新 AI 判定日誌
                self.router.update_db_log(new_media_id, "OUTPUT", True, score)
                logger.info(f"📸 證據截圖動態存檔成功: {new_filename} (ID: {new_media_id})")
                
            finally:
                conn.close()
                
        except Exception as e:
            logger.error(f"❌ 證據截圖存檔失敗: {e}")