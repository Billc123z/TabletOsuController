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
- **電腦端**：請至專案的 **Releases** 區下載打包好的 `v2_sever.rar`（或 `Server.zip`），裡面已經內建了專用的 Godot 引擎與 ADB 環境，**完全免安裝，隨插即用**！
- **平板端**：請至專案的 **Releases** 區下載最新的 `Client.apk`，並安裝到你的平板上。

---

## 🚀 使用方法

### 1. 準備工作
- 你的 Android 平板需要開啟 **開發人員選項** 以及 **USB 偵錯**。
- 將平板透過 USB 連接到電腦。
- 建議在平板的 USB 設定中開啟「USB 網路共用 (USB Tethering)」來建立區域網路，降低網路延遲。

### 2. 開啟 Server (電腦端)
我們準備了方便的啟動腳本，且所有必備環境（引擎、ADB）都已打包完成。

1. 將下載回來的伺服器壓縮檔解壓縮到任意資料夾內。
2. 直接點擊執行資料夾中的 **`StartServer.bat`**。
3. 伺服器介面會瞬間啟動，並在介面上提供即時的筆尖狀態與座標監控。成功啟動後會顯示 `Waiting for Android client on TCP :42425`。

### 3. 開啟 Client (平板端)
- 在平板打開已經安裝好的 Client APK。
- 輸入電腦的區域網路 IP（如果是 USB 網路共用，通常會是類似 `192.168.42.x` 的 IP）。
- 按下 **Apply & Connect**。

此時電腦端會瞬間啟動背景的 `adb shell getevent`，接管平板的 S-Pen 輸入，你現在可以開始打 osu! 了！

## 🖊️ 按鍵映射與智慧觸控邏輯

為了完美支援 osu! 的「拖曳流」玩家（筆尖貼著板子滑動），我們實作了如同筆電觸控板等級的智慧防誤觸機制：
- **純移動 (Move)**：筆尖碰到螢幕並在 200ms 內開始滑動。此時**不會**觸發左鍵，維持純粹的游標移動（無按鍵干擾）。
- **快速點擊 (Tap)**：筆尖輕觸螢幕，並在 200ms 內抬起。觸發一次滑鼠左鍵點擊。
- **長按拖曳 (Drag)**：筆尖觸碰螢幕並停頓滿 200ms 再滑動。自動按住滑鼠左鍵，支援 Windows 視窗拖曳。
- **側邊按鈕 (`BTN_STYLUS`)**：按下筆身側鍵 = 滑鼠右鍵。
- **筆尖懸浮 (`BTN_TOOL_PEN`)**：純粹的滑鼠移動。
