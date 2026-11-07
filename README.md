# Amuse 桌面悬浮窗

把 6K Labs Amuse 的浏览器源（browser source）直接投射到 Windows 桌面上：
透明背景、置顶、无任务栏图标，只留一个托盘图标。
适用于日常，游戏直播等。（直播时使用OBS源可能导致ACE扫后台造成误封）

<img width="2047" height="1445" alt="image" src="https://github.com/user-attachments/assets/7de4822c-d871-4ee5-ac59-a84fae13e149" />



## 系统要求

- Windows 10 / 11（64 位）
- [Microsoft Edge WebView2 运行时](https://developer.microsoft.com/microsoft-edge/webview2/)
  一般 Win11 已自带；Win10 如启动提示缺少 WebView2，安装一次即可。
- .NET Framework 4.8（Win10 1903+ / Win11 自带）

## 准备工作：获取 Amuse 浏览器源地址

使用前需要先取得自己的 Amuse widget 地址：

1. 安装 **网易云音乐 BetterNCM**。
2. 在 BetterNCM 中安装 **6K Labs** 插件。
3. 打开并登录 [6klabs.com](https://6klabs.com)，进入 **Widgets → Amuse**。
4. 选择 **Pear Desktop（YouTube Music）**。
5. 复制生成的 URL。

拿到 URL 后，右键托盘图标 →“打开配置文件”，把地址填到 `config.json`
的 `url` 字段里，保存即可自动生效：

```json
{
  "url": "https://6klabs.com/widget/amuse/你的地址..."
}
```

## 运行

双击 `dist\AmuseOverlay\AmuseOverlay.exe`。

- 窗口默认在桌面右上角，鼠标穿透（锁定）状态。
- 不会出现在任务栏或 Alt+Tab 中，只在托盘显示一个橙色图标。
- 需要分发时，把整个 `dist\AmuseOverlay` 文件夹一起拷走即可
  （`AmuseOverlay.exe` + 3 个 DLL）。

## 托盘菜单

右键单击托盘图标：

- 显示 / 隐藏悬浮窗
- 锁定（鼠标穿透）
- 窗口置顶
- 重新加载网页
- 重置位置和大小
- 在浏览器中打开源地址
- 打开配置文件
- 退出

快捷操作：

- **双击托盘图标** = 锁定 / 解锁（鼠标穿透开关）
- **右键双击托盘图标** = 显示悬浮窗，并取消置顶
- **右键单击托盘图标** = 打开菜单

## 调整位置和大小

按下 `Ctrl+Alt+L`（或双击托盘图标、或取消托盘菜单里的“锁定”）后进入编辑状态：

- 拖动窗口顶部的深色条：移动位置
- 拖动窗口四周边缘或四个角：自由缩放
- 调整完成后再次按 `Ctrl+Alt+L` 锁定，鼠标会重新穿透到桌面

## 快捷键

| 快捷键 | 功能 |
| --- | --- |
| `Ctrl+Alt+L` | 锁定 / 解锁（鼠标穿透开关） |
| `Ctrl+Alt+H` | 显示 / 隐藏悬浮窗 |

## 配置文件

默认路径：

```
%APPDATA%\AmuseOverlay\config.json
```

也可以用 `--config <路径>` 指定其他配置文件。文件被修改后会自动热重载
（适合让其他插件同步更新 URL 或位置）。

字段说明：

| 字段 | 说明 |
| --- | --- |
| `url` | 要显示的浏览器源地址 |
| `x` / `y` | 窗口左上角坐标（逻辑像素，`-1` 表示自动放到右上角） |
| `width` / `height` | 窗口大小（逻辑像素） |
| `opacity` | 整体不透明度，`0.08` ~ `1.0` |
| `zoom` | 网页缩放比例，`0.25` ~ `5.0` |
| `clickThrough` | `true` = 锁定/鼠标穿透；`false` = 可拖动缩放 |
| `alwaysOnTop` | 是否置顶 |
| `muted` | 是否静音 |
| `visible` | 是否显示 |

坐标和尺寸使用逻辑像素，程序会按系统 DPI（如 150%）自动换算，
在不同缩放比例的显示器上看起来大小一致。

## 开机自启

1. 按 `Win+R`，输入 `shell:startup` 回车。
2. 把 `AmuseOverlay.exe` 的快捷方式放进打开的文件夹。

## 从源码构建

```powershell
# 首次构建前下载 WebView2 SDK
powershell -ExecutionPolicy Bypass -File .\download-vendor.ps1

# 编译到 dist\AmuseOverlay
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

构建只依赖系统自带的 .NET Framework 4.8 `csc.exe`，不需要安装 .NET SDK。

## 日志

启动异常或网页加载失败时查看：

```
%APPDATA%\AmuseOverlay\overlay.log
```
