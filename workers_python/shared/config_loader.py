import sys
import logging
from pathlib import Path
from dotenv import load_dotenv

logger = logging.getLogger(__name__)

def setup_project_env():
    """
    自動偵測專案根目錄，載入 .env 並配置 sys.path。
    回傳專案根目錄的 Path 物件。
    """
    # 此檔案位於 workers_python/shared/，向上三層即為根目錄
    base_path = Path(__file__).resolve().parent.parent.parent
    env_path = base_path / '.env'

    if env_path.exists():
        load_dotenv(dotenv_path=env_path)
    else:
        logger.warning(f"⚠️ 找不到 .env 檔案於: {env_path}")

    # 統一處理 Python 模組路徑
    workers_path = str(base_path / "workers_python")
    root_path = str(base_path)

    if workers_path not in sys.path: sys.path.insert(0, workers_path)
    if root_path not in sys.path: sys.path.insert(0, root_path)

    return base_path