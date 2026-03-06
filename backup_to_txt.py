import os

# ================= 配置設定 =================
# 1. 您專案的根目錄
src_dir = r"C:\Users\吳仲霖\Desktop\PythonScript\ig\ig_ai_vision_system"

# 2. 輸出的整合檔案名稱 (放在桌面)
output_file = r"C:\Users\吳仲霖\Desktop\full_project_context.txt"

# 3. 要排除的自動生成資料夾
exclude_dirs = {'bin', 'Properties', 'obj', '.vs', '.vscode', 'node_modules', '.git', '__pycache__'}

# 4. 包含的純文字程式碼副檔名
include_extensions = {'.cs', '.ts', '.py', '.vue', '.sql', '.json', '.txt', '.env', '.md'}
# ============================================

def perform_single_file_backup():
    count = 0
    with open(output_file, 'w', encoding='utf-8') as outfile:
        for root, dirs, files in os.walk(src_dir):
            # 🌟 過濾掉不需要的資料夾
            dirs[:] = [d for d in dirs if d not in exclude_dirs]

            for file in files:
                ext = os.path.splitext(file)[1].lower()
                if ext in include_extensions:
                    full_path = os.path.join(root, file)
                    # 🌟 計算相對路徑
                    relative_path = os.path.relpath(full_path, src_dir)
                    
                    try:
                        with open(full_path, 'r', encoding='utf-8') as infile:
                            content = infile.read()
                            
                        # 🌟 依照您的要求，在每個檔案開頭輸出路徑標籤
                        outfile.write(f"\nFILE: {relative_path}\n")
                        outfile.write(f"{'='*40}\n")
                        outfile.write(content)
                        outfile.write(f"\n\n") # 檔案間留空行
                        
                        print(f"✅ 已整合: {relative_path}")
                        count += 1
                    except Exception as e:
                        print(f"❌ 讀取失敗 {relative_path}: {e}")

    print(f"\n✨ 整合完成！共處理 {count} 個檔案。")
    print(f"📍 整合檔案路徑: {output_file}")

if __name__ == "__main__":
    perform_single_file_backup()