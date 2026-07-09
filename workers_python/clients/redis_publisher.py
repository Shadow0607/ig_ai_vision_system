import redis
import json
import os
import logging
import time

logger = logging.getLogger(__name__)

class RedisPublisher:
    def __init__(self):
        # 建立 Redis 連線，自動讀取 .env 中的設定
        self.redis = redis.Redis(
            host=os.getenv('REDIS_HOST', 'localhost'),
            port=int(os.getenv('REDIS_PORT', 6379)),
            password=os.getenv('REDIS_PASSWORD', ''),
            decode_responses=True
        )

    def push_task(self, payload: dict, is_priority: bool = False):
        """🌟 雙車道分流：根據 is_priority 決定推送到哪一個佇列"""
        # 急件去 high，一般件去原本的 queue
        queue_name = "ig_processing_queue_high" if is_priority else "ig_processing_queue"
        
        try:
            self.redis.lpush(queue_name, json.dumps(payload))
            # 方便 Debug 看任務去了哪裡 (不需要可註解掉)
            # logger.debug(f"已推送任務至 {queue_name}") 
        except Exception as e:
            logger.error(f"❌ 推送任務至 Redis 失敗: {e}")

    def get_queue_length(self) -> int:
        """🌟 計算總積壓任務數量 (急件 + 一般件)"""
        try:
            high_len = self.redis.llen("ig_processing_queue_high")
            normal_len = self.redis.llen("ig_processing_queue")
            return high_len + normal_len
        except Exception as e:
            logger.error(f"⚠️ 無法取得 Redis 佇列長度: {e}")
            return 0

    def throttle_if_queue_full(self, max_size: int = 3):
        """🚦 煞車機制：如果兩條車道總和塞滿了，就暫停抓取"""
        while self.get_queue_length() >= max_size:
            logger.warning(f"🚦 AI 佇列已滿 (目前 {self.get_queue_length()} 筆)，暫停爬蟲下載，等待 AI 消化...")
            time.sleep(5)