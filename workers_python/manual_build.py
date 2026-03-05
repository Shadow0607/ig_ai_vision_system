import sys
import os

# 確保能 import 同目錄下的 s2_to_s4_ai_consumer
sys.path.append(os.path.dirname(os.path.abspath(__file__)))

from s2_to_s4_ai_consumer import AIWorker

def main():
    # 1. 檢查參數：使用者有沒有輸入人物名稱？
    if len(sys.argv) < 2:
        print("❌ 用法錯誤！請輸入人物的 SystemName")
        print("範例: python manual_build.py yyyoungggggg")
        sys.exit(1)

    target_system_name = sys.argv[1]

    print("=================================================")
    print(f"🔧 [手動模式] 正在為 '{target_system_name}' 重建特徵庫...")
    print("=================================================")

    try:
        # 2. 初始化 AI Worker (會自動載入模型與資料庫設定)
        worker = AIWorker()
        
        # 3. 強制執行建檔函式
        #這會同時掃描 pos (正向) 和 garbage (負向) 資料夾
        worker.build_feature_bank(target_system_name)
        
        print("\n✅ 執行完畢！")
        print(f"請檢查 storage_volumes/{target_system_name}/ 目錄：")
        print("  - pos_features.npy (應存在)")
        print("  - neg_features.npy (若有垃圾樣本，應存在)")

    except Exception as e:
        print(f"\n❌ 發生錯誤: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()