import os
import shutil

# ================= 配置設定 =================
# 1. 來源專案根目錄
src_dir = r"C:\Users\吳仲霖\Desktop\PythonScript\ig\ig_ai_vision_system"

# 2. 輸出的目標資料夾 (所有 txt 都會丟到這裡)
output_base_dir = r"C:\Users\吳仲霖\Desktop\Project_Files_Backup"

# 3. 要排除的資料夾
exclude_dirs = {'bin', 'Properties', 'obj', '.vs', '.vscode', 'node_modules', '.git', '__pycache__'}

# 4. 包含的副檔名
include_extensions = {'.cs', '.ts', '.py', '.vue', '.sql', '.json', '.txt', '.env', '.md','.js','.html','.css','.scss','.less','.xml','.yml','.yaml','.ini','.cfg','.config'}
# ============================================

def perform_flat_txt_backup():
    if not os.path.exists(output_base_dir):
        os.makedirs(output_base_dir)
        print(f"📁 已建立輸出目錄: {output_base_dir}")

    count = 0
    for root, dirs, files in os.walk(src_dir):
        # 排除不需要的資料夾
        dirs[:] = [d for d in dirs if d not in exclude_dirs]

        for file in files:
            ext = os.path.splitext(file)[1].lower()
            if ext in include_extensions:
                full_path = os.path.join(root, file)
                
                # 🌟 取得相對路徑 (例如: state_management\db_repository.py)
                rel_path = os.path.relpath(full_path, src_dir)
                
                # 🌟 產生新的檔名：將路徑中的 \ 換成 _ 避免重複，並加上 .txt
                # 例如: state_management_db_repository.py.txt
                new_filename = rel_path.replace(os.sep, "_") + ".txt"
                target_path = os.path.join(output_base_dir, new_filename)

                try:
                    with open(full_path, 'r', encoding='utf-8') as infile:
                        content = infile.read()

                    # 🌟 寫入新檔案
                    with open(target_path, 'w', encoding='utf-8') as outfile:
                        # 第一行存原本的結構路徑，方便以後辨識
                        outfile.write(f"ORIGINAL_PATH: {rel_path}\n")
                        outfile.write(f"{'='*40}\n\n")
                        outfile.write(content)
                    
                    print(f"✅ 已轉換: {rel_path} -> {new_filename}")
                    count += 1
                except Exception as e:
                    print(f"❌ 處理失敗 {rel_path}: {e}")

    print(f"\n✨ 備份完成！共生成 {count} 個 .txt 檔案。")
    print(f"📍 所有檔案位於: {output_base_dir}")

if __name__ == "__main__":
    perform_flat_txt_backup()