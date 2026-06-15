# osu! Tablet Controller (ADB Evdev Edition)

這是一個能讓你的 Android 平板變成 **極低延遲電競級 osu! 繪圖板** 的專案（實測 Samsung Galaxy Tab S10 FE 的 S-Pen 輪詢率可達 500Hz）。

## ✨ 特色與突破

一般使用 Android App 來做繪圖板映射時，觸控事件會被 Android 系統的 `InputDispatcher` 強制限流到螢幕的刷新率（例如 90Hz 或 120Hz）。

這個專案透過 **V2 架構完全繞過了 Android 系統的限制**：
1. **平板端 (Godot Client)**：只負責提供 UI 讓你視覺化設定「觸控對應區域」的範圍，並透過 TCP 傳送給電腦。
2. **電腦端 (Godot/C# Server)**：直接透過 USB (ADB) 讀取平板底層 Linux Kernel 的 `evdev` 裝置，以硬體極限頻率攔截 S-Pen 的原生訊號。
3. **底層注入**：使用 Windows `mouse_event` API (伴隨 `MOUSEEVENTF_ABSOLUTE`) 將滑鼠訊號注入系統，避免被 Windows DWM (桌面視窗管理員) 降速，讓 osu! 完美吃到真實的高頻輸入。

> ⚠️ **注意：本專案目前專為 S-Pen 設計，不支援純手指觸控。**

## 📥 下載與安裝

### 系統需求
- **電腦端**：需準備 [Godot 4.2.2 Mono 版 (.NET版)](https://godotengine.org/download/archive/4.2.2-stable/)。這個專案有使用 C# 與 Windows 底層 API 互動，**必須使用 4.2.2 的 Mono 版本** 才能正常編譯執行。
- **平板端**：你可以直接下載我們準備好的預先編譯版本，不需要自己拿 Godot 匯出！請至專案的 **Releases** 區下載最新的 `Client.apk`，並安裝到你的平板上。

---

## 🚀 使用方法

### 1. 準備工作
- 你的 Android 平板需要開啟 **開發人員選項** 以及 **USB 偵錯**。
- 將平板透過 USB 連接到電腦。
- 建議在平板的 USB 設定中開啟「USB 網路共用 (USB Tethering)」來建立區域網路，降低網路延遲。

### 2. 開啟 Server (電腦端)
我們準備了方便的啟動腳本，你**不需要**每次都打開 Godot 編輯器。

1. 將下載回來的 Godot 引擎「**整個資料夾**」解壓縮。*(注意：裡面會有一個 `GodotSharp` 資料夾，它是 C# 必備的核心，千萬不能只把 exe 單獨拉出來！)*
2. 確保 `Godot_v4.2.2-stable_mono_win64.exe` 跟 `GodotSharp` 資料夾放在一起，並將它們丟進這個專案的資料夾裡面（放哪一層都沒關係，啟動腳本會自己找）：
   ```text
   TabletOsuController/
   ├── Godot_v4...exe     <-- 不能單獨存在
   ├── GodotSharp/        <-- 必須跟 exe 放在一起！
   ├── scenes/
   ├── scripts/
   ├── StartServer.bat    <-- 點擊這個啟動
   └── TabletOsuController.csproj
   ```
3. 確定引擎跟 GodotSharp 放進來後，直接點擊執行 **`StartServer.bat`**。
4. 腳本會自動遞迴搜尋旁邊的 Godot 引擎並啟動伺服器介面，成功的話你會看到畫面顯示 `Waiting for Android client on TCP :42425`。

### 3. 開啟 Client (平板端)
- 在平板打開已經安裝好的 Client APK。
- 輸入電腦的區域網路 IP（如果是 USB 網路共用，通常會是類似 `192.168.42.x` 的 IP）。
- 按下 **Apply & Connect**。

此時電腦端會瞬間啟動背景的 `adb shell getevent`，接管平板的 S-Pen 輸入，你現在可以開始打 osu! 了！

## 🖊️ 按鍵映射對應

本專案經過精密解析，將 S-Pen 的事件精準對應：
- **筆尖碰到螢幕 (`BTN_TOUCH`)** = 滑鼠左鍵 (支援點擊與長按拖曳)
- **按下筆身側邊按鈕 (`BTN_STYLUS`)** = 滑鼠右鍵
- **筆尖懸浮 (`BTN_TOOL_PEN`)** = 純粹的滑鼠移動，無按鍵觸發
