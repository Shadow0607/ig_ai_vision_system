import os
import json
import logging
from pathlib import Path

logger = logging.getLogger(__name__)

class CheckpointTracker:
    def __init__(self, base_storage_path: Path):
        self.checkpoint_file = base_storage_path / 'instagram_checkpoints.json'
        self.pinned_posts_file = base_storage_path / 'pinned_posts.json'
        self.checkpoints = self._load_json(self.checkpoint_file)
        self.pinned_posts = self._load_json(self.pinned_posts_file)

    def _load_json(self, filepath: Path) -> dict:
        if os.path.exists(filepath):
            try:
                with open(filepath, 'r', encoding='utf-8') as f: 
                    return json.load(f)
            except Exception as e:
                logger.warning(f"讀取 {filepath} 失敗: {e}")
        return {}

    def _save_json(self, data: dict, filepath: Path):
        try:
            with open(filepath, 'w', encoding='utf-8') as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
        except Exception as e:
            logger.error(f"存檔失敗 {filepath}: {e}")

    def is_post_pinned_safe(self, post) -> bool:
        if hasattr(post, 'is_pinned') and post.is_pinned: return True
        try:
            if 'pinned_for_users' in post._node and post._node['pinned_for_users']: return True
        except: pass
        return False

    def update_pinned_list(self, username: str, shortcode: str):
        if username not in self.pinned_posts:
            self.pinned_posts[username] = []
        if shortcode not in self.pinned_posts[username]:
            logger.info(f"📌 自動記錄置頂貼文: {shortcode}")
            self.pinned_posts[username].append(shortcode)
            self._save_json(self.pinned_posts, self.pinned_posts_file)

    def get_checkpoint(self, system_name: str, account: str) -> str:
        return self.checkpoints.get(f"{system_name}:{account}")

    def save_checkpoint(self, system_name: str, account: str, new_checkpoint: str):
        key = f"{system_name}:{account}"
        if new_checkpoint and self.checkpoints.get(key) != new_checkpoint:
            logger.info(f"💾 更新檢查點: {self.checkpoints.get(key)} -> {new_checkpoint}")
            self.checkpoints[key] = new_checkpoint
            self._save_json(self.checkpoints, self.checkpoint_file)