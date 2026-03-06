import os
import redis
import logging

logger = logging.getLogger(__name__)

class SysStatusManager:
    _instance = None
    _statuses = {} # 記憶體快取字典：{ 'CATEGORY': { 'CODE': ID } }

    def __new__(cls, *args, **kwargs):
        if not cls._instance:
            cls._instance = super(SysStatusManager, cls).__new__(cls)
            cls._instance._load_statuses_from_redis()
        return cls._instance

    def _load_statuses_from_redis(self):
        """🚀 [Cloud-Native] 啟動時瞬間從 Redis 陣列拉取 sys:statuses"""
        logger.info("⚡ 正在從 Redis 快取節點拉取系統狀態字典...")
        try:
            pool = redis.ConnectionPool(
                host=os.getenv('REDIS_HOST', 'localhost'),
                port=int(os.getenv('REDIS_PORT', 6379)),
                password=os.getenv('REDIS_PASSWORD'),
                decode_responses=True # 自動將 byte 解碼成字串
            )
            r = redis.Redis(connection_pool=pool)

            # 一次性拉取整個 Hash 表 (極速操作 O(N))
            raw_dict = r.hgetall("sys:statuses")
            
            if not raw_dict:
                logger.warning("⚠️ Redis 中尚未建立 sys:statuses 快取！請確認 C# 後端已啟動。")
                return

            for key, val in raw_dict.items():
                # Redis Hash 的 Key 格式為 "CATEGORY:CODE"，例如 "AI_RECOGNITION:OUTPUT"
                cat, code = key.split(":")
                idx = int(val)

                if cat not in self._statuses:
                    self._statuses[cat] = {}
                self._statuses[cat][code] = idx

            total_loaded = sum(len(v) for v in self._statuses.values())
            logger.info(f"✅ Redis 字典極速載入完成，共 {total_loaded} 筆狀態，Python Worker 準備就緒！")

        except Exception as e:
            logger.error(f"❌ 從 Redis 載入狀態字典失敗: {e}")

    def get_id(self, category: str, code: str) -> int:
        """透過 Category 和 Code 從記憶體取得對應的 ID"""
        try:
            return self._statuses[category.upper()][code.upper()]
        except KeyError:
            logger.error(f"⚠️ 找不到對應的狀態 ID: Category={category}, Code={code}")
            return None