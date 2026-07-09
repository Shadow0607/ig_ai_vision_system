import hashlib
import imagehash
from PIL import Image
from io import BytesIO

class HashHelper:
    @staticmethod
    def get_dual_fingerprints(file_bytes: bytes, filename: str):
        """
        一次回傳 (pHash, MD5) 的雙指紋：
        - pHash: 僅針對圖片運算感知雜湊，若為影片則預設與 MD5 相同。
        - MD5: 所有檔案的絕對二進位特徵，用於精確防撞。
        """
        if not file_bytes: return None, None
        
        md5_hash = hashlib.md5(file_bytes).hexdigest()
        p_hash = None
        
        ext = filename.lower()
        if ext.endswith(('.jpg', '.jpeg', '.png', '.webp')):
            try:
                img = Image.open(BytesIO(file_bytes))
                p_hash = str(imagehash.phash(img))
            except Exception:
                p_hash = md5_hash # 圖片損壞降級為 MD5
        else:
            p_hash = md5_hash # 影片的 pHash 直接使用 MD5
            
        return p_hash, md5_hash