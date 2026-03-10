import instaloader
import glob
import os
from platform import system

def get_firefox_cookiefile():
    # 根據作業系統尋找 Firefox Profiles 路徑
    if system() == "Windows":
        pattern = os.path.expanduser("~\\AppData\\Roaming\\Mozilla\\Firefox\\Profiles\\*\\cookies.sqlite")
    else:
        pattern = os.path.expanduser("~/Library/Application Support/Firefox/Profiles/*/cookies.sqlite")
    
    files = glob.glob(pattern)
    if not files:
        return None
    # 傳回最新修改的那個 cookie 檔案
    return max(files, key=os.path.getmtime)

L = instaloader.Instaloader()
cookie_path = get_firefox_cookiefile()

if cookie_path:
    try:
        # 改用這種方式載入，確保路徑正確
        L.load_session_from_browser("firefox", cookie_path)
        print(f"✅ 成功載入 Cookie: {cookie_path}")
    except Exception as e:
        print(f"❌ 載入失敗: {e}")
else:
    print("❌ 完全找不到 Firefox 的 cookies.sqlite，請確認已安裝 Firefox 並登入 IG")

PROFILE = "54jojo1208"
try:
    profile = instaloader.Profile.from_username(L.context, PROFILE)
    has_stories = False
    for story in L.get_stories(userids=[profile.userid]):
        has_stories = True
        for item in story.get_items():
            print(f"找到 Story ID: {item.mediaid}")
    if not has_stories:
        print("查無任何限時動態，可能是時效已過或被 IG 阻擋。")
except Exception as e:
    print(f"發生錯誤: {e}")