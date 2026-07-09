import logging
import redis

logger = logging.getLogger(__name__)

class CheckpointTracker:
    def __init__(self, redis_client: redis.Redis):
        self.redis = redis_client
        self.chk_key = "ig_ai:checkpoints"
        self.pin_key = "ig_ai:pinned_posts" # 🌟 改為 Set 結構的 Key 前綴

    def is_post_pinned_safe(self, post) -> bool:
        if hasattr(post, 'is_pinned') and post.is_pinned: return True
        try:
            if 'pinned_for_users' in post._node and post._node['pinned_for_users']: return True
        except: pass
        return False

    def update_pinned_list(self, username: str, shortcode: str):
        # 🌟 雲原生化：使用 Redis Set (集合) 確保原子性與防重複，解決多容器併發衝突
        set_key = f"{self.pin_key}:{username}"
        is_new = self.redis.sadd(set_key, shortcode)
        
        if is_new:
            logger.info(f"📌 自動記錄置頂貼文至 Redis Set: {shortcode}")
            # 設定過期時間 (例如 7 天)，避免死資料無限堆積
            self.redis.expire(set_key, 86400 * 7)

    def is_already_pinned(self, username: str, shortcode: str) -> bool:
        """檢查是否已經在 Redis 的置頂名單中"""
        return self.redis.sismember(f"{self.pin_key}:{username}", shortcode)

    def get_checkpoint(self, system_name: str, account: str) -> str:
        res = self.redis.hget(self.chk_key, f"{system_name}:{account}")
        return res if res else None

    def save_checkpoint(self, system_name: str, account: str, new_checkpoint: str):
        key = f"{system_name}:{account}"
        old_chk = self.redis.hget(self.chk_key, key)
        
        if new_checkpoint and old_chk != new_checkpoint:
            logger.info(f"💾 雲端更新檢查點: {old_chk} -> {new_checkpoint}")
            self.redis.hset(self.chk_key, key, new_checkpoint)