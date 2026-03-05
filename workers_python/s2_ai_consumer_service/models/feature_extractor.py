import os
import cv2
import numpy as np
import logging
from deepface import DeepFace

logger = logging.getLogger(__name__)

class FeatureExtractor:
    def __init__(self, detector_backend: str = "retinaface"):
        self.detector_backend = detector_backend
        logger.info(f"🧠 初始化特徵提取器 (Backend: {self.detector_backend})")
        self._warmup_model()

    def _warmup_model(self):
        """模型暖身：預先載入 DeepFace 權重到記憶體，加速後續處理"""
        try:
            dummy = np.zeros((224, 224, 3), dtype=np.uint8)
            DeepFace.represent(
                dummy, 
                model_name="VGG-Face", 
                enforce_detection=False, 
                detector_backend=self.detector_backend
            )
            logger.info("✅ DeepFace 模型暖身完成")
        except Exception as e:
            logger.warning(f"⚠️ 模型暖身失敗，將在首次請求時載入: {e}")

    def read_image(self, local_path: str) -> np.ndarray:
        """安全讀取圖片 (支援含有中文或特殊字元的本地路徑)"""
        try:
            if not os.path.exists(local_path): 
                return None
            img_np = np.fromfile(local_path, dtype=np.uint8)
            img = cv2.imdecode(img_np, cv2.IMREAD_COLOR)
            return img
        except Exception as e:
            logger.error(f"❌ 圖片讀取失敗 {local_path}: {e}")
            return None

    def get_video_frame(self, local_path: str) -> np.ndarray:
        """[升級版] 嘗試從影片的多個時間點擷取有效的人臉影格"""
        try:
            cap = cv2.VideoCapture(local_path)
            if not cap.isOpened():
                return None
            
            fps = cap.get(cv2.CAP_PROP_FPS)
            if fps <= 0: fps = 30
            
            # 嘗試的時間點：第 1.0, 2.5, 5.0 秒，避開開頭的黑屏或 Logo
            sample_seconds = [1.0, 2.5, 5.0]
            
            for sec in sample_seconds:
                frame_idx = int(fps * sec)
                cap.set(cv2.CAP_PROP_POS_FRAMES, frame_idx)
                success, frame = cap.read()
                
                if success and frame is not None:
                    # 檢查影格是不是全黑 (平均亮度 > 10)
                    if np.mean(frame) > 10:
                        cap.release()
                        return frame
            
            cap.release()
            return None
        except Exception as e:
            logger.error(f"🎥 影片抽幀失敗: {e}")
            return None

    def extract_target_embedding(self, img_array: np.ndarray) -> tuple:
        """
        執行 AI 人臉偵測與特徵提取 (包含潛在的旋轉補償)
        回傳: (target_embedding: list 或是 None, face_detected: bool)
        """
        if img_array is None:
            return None, False

        target_embedding = None
        face_detected = False
        
        # 保留未來可能擴充的旋轉變體邏輯
        variants = [("原始", img_array)]
        
        for label, img_var in variants:
            try:
                embeds = DeepFace.represent(
                    img_var, 
                    model_name="VGG-Face", 
                    enforce_detection=True, 
                    detector_backend=self.detector_backend
                )
                if embeds: 
                    target_embedding = embeds[0]["embedding"]
                    face_detected = True
                    if label != "原始": 
                        logger.info(f"🔄 旋轉補償成功 ({label})")
                    break 
            except ValueError:
                # 找不到臉 (DeepFace Enforce Detection 觸發的 ValueError)
                continue 
            except Exception as e:
                logger.error(f"❌ AI 辨識意外錯誤: {e}")
                break

        return target_embedding, face_detected