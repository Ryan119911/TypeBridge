# TypeBridge

<img src="assets/typebridge-128.png" width="96" alt="TypeBridge icon">

**在 Windows 上直接打字，把键盘输入实时发送到 Ubuntu 当前焦点窗口。**

TypeBridge 是一个无窗口、托盘常驻的 Windows 输入桥。它转发键盘和鼠标输入，不传画面。中文不在 Windows 端提交，而是把拼音和候选选择键送到 Ubuntu，由 **Ubuntu 输入法**完成组词和选字。

[![Build TypeBridge](https://github.com/Ryan119911/TypeBridge/actions/workflows/build.yml/badge.svg)](https://github.com/Ryan119911/TypeBridge/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/Ryan119911/TypeBridge)](https://github.com/Ryan119911/TypeBridge/releases)

## 下载

- Windows：`TypeBridge.exe`
- Ubuntu 22.04（图形安装）：`typebridge_<版本>_all.deb`
- 最新版本：<https://github.com/Ryan119911/TypeBridge/releases/latest>
- 项目源码：<https://github.com/Ryan119911/TypeBridge>

每台 Windows 都连接**它自己填写的 Ubuntu**。项目没有中央服务器，也不会连接到发布者的电脑。

Ubuntu 安装包会创建应用菜单入口和开机自启的 systemd 用户服务。安装完成后不需要在 Ubuntu 终端执行命令。

## 特性

- **无输入窗口**：启动后只常驻系统托盘，不打断正常 Windows 输入。
- **全局切换**：在任何软件中按快捷键（默认 `Ctrl + Alt + U`），在 Windows 输入和 Ubuntu 输入之间切换。
- **快捷键可改**：设置窗口内点击输入框，直接按下想要的组合键即可录制，无需手改配置文件。
- **键盘/鼠标联动可选**：默认一个快捷键同时切换键盘和鼠标；也可以取消联动，用两个独立快捷键分别切换。
- **即时状态提示**：复用单条非激活浮层，连续切换时立即刷新，不会像系统气泡那样排队延迟。
- **Ubuntu 输入法选字**：拼音字母、数字、空格发往 Ubuntu，候选窗口由 Ubuntu 输入法显示。
- **发送后不堆字**：Ubuntu 模式下的按键不会同时进入 Windows 当前软件。
- **特殊按键**：方向键、PageUp/PageDown、Esc、Tab、Insert。
- **全键盘转发**：独立 Ctrl/Shift/Alt/Win、F1–F24、CapsLock/NumLock、小键盘、PrintScreen 和媒体键。
- **鼠标转发**：接管鼠标时，移动、左/中/右键、侧键、滚轮都送到 Ubuntu（见「鼠标支持」）。
- **组合键**：Ctrl/Alt/Win + 字母、数字、功能键，例如 `Ctrl+A`、`Alt+B`、`Win+E`。
- **独立配置**：首次运行填入自己的 Ubuntu IP 和端口，保存在本地 `typebridge.ini`。
- **低依赖**：Windows 端使用系统自带 .NET Framework；Ubuntu 端只需要 Python 3、GTK、xdotool 和可用的 X11 输入法。
- **Ubuntu 图形设置页**：`.deb` 安装后从应用菜单打开，可查看服务状态、显示本机 IP、复制地址、修改端口和启停服务。
- **单实例**：重复启动会自动退出第二个实例，避免重复键盘钩子。

## 架构

```text
┌──────────────────────────────┐
│ Windows 10/11                │
│                              │
│  TypeBridge.exe              │
│    ├─ 托盘图标 / 设置窗口     │
│    ├─ 可配置模式切换热键     │
│    └─ 低级键盘/鼠标钩子        │
└──────────────┬───────────────┘
               │ TCP 4321
               │ Tailscale / 局域网
               ▼
┌──────────────────────────────┐
│ Ubuntu 22.04 (X11)           │
│                              │
│  typebridge.py               │
│    ├─ 行协议服务器            │
│    ├─ xdotool 注入按键        │
│    └─ Ubuntu 输入法处理拼音   │
└──────────────┬───────────────┘
               ▼
       Ubuntu 当前焦点窗口
```

### 两种输入模式

| 模式 | 行为 |
|---|---|
| Windows | 键盘钩子不拦截任何按键，Windows 软件正常使用 |
| Ubuntu | 拦截字符、特殊键和组合键，发送到 Ubuntu，并阻止其进入 Windows |

托盘图标和提示文字会显示当前模式和连接状态。

## 快速开始

### 1. 安装 Ubuntu 端（推荐：图形安装）

1. 从 Releases 下载 `typebridge_<版本>_all.deb`。
2. 双击安装包，在“软件安装”中点击安装，输入一次管理员密码。
3. 安装完成后，在 Ubuntu 应用菜单中搜索并打开 **TypeBridge**。
4. 设置页会显示服务状态、本机 IP、端口和连接信息。

Ubuntu 设置页可以完成：

- 查看服务是否运行；
- 查看自动启动状态；
- 复制 Windows 端需要填写的 IP；
- 启动、停止、重新启动服务；
- 修改 TCP 端口并保存；
- 通过系统授权窗口放行 ufw 防火墙端口。

日用不需要执行 `systemctl` 或 Python 命令。

#### 手动安装（开发者备选）

要求：

- Ubuntu 22.04，X11 会话（Wayland 下 xdotool 无法可靠注入按键）
- 已启用可用的输入法，例如 fcitx + 拼音/搜狗输入法
- 与 Windows 在同一局域网，或处在同一个 Tailscale tailnet

安装依赖：

```bash
sudo apt update
sudo apt install -y python3-gi gir1.2-gtk-3.0 xdotool
```

安装服务脚本：

```bash
install -Dm755 typebridge-ubuntu.py ~/.local/bin/typebridge.py
mkdir -p ~/.config/systemd/user
```

创建 `~/.config/systemd/user/typebridge.service`：

```ini
[Unit]
Description=TypeBridge - Windows to Ubuntu keyboard bridge
PartOf=graphical-session.target
After=graphical-session.target

[Service]
ExecStart=/usr/bin/python3 %h/.local/bin/typebridge.py
Restart=on-failure
RestartSec=2
Environment=DISPLAY=:0
Environment=XAUTHORITY=%t/gdm/Xauthority
Environment=GTK_IM_MODULE=fcitx
Environment=QT_IM_MODULE=fcitx
Environment=XMODIFIERS=@im=fcitx

[Install]
WantedBy=graphical-session.target
```

如果使用 ibus，请把三处 `fcitx` 改为 `ibus`；如果 `XAUTHORITY` 路径不同，按实际会话调整。

启动并检查：

```bash
systemctl --user daemon-reload
systemctl --user enable --now typebridge
systemctl --user status typebridge --no-pager
```

Ubuntu 端配置保存在 `~/.config/typebridge/config.json`：

```json
{
  "port": 4321,
  "auto_deactivate_fcitx": false
}
```

`auto_deactivate_fcitx` 必须为 `false`，否则发送前会关闭 fcitx，拼音无法进入 Ubuntu 输入法。修改配置后重启服务。

如需从局域网访问，放行端口：

```bash
sudo ufw allow 4321/tcp
```

> 只应在可信局域网或 Tailscale 内放行。不要把这个端口直接暴露到公网。

获取 Ubuntu 地址：

```bash
tailscale ip -4
```

例如输出 `100.x.y.z`。也可以使用局域网 IP 或主机名。图形设置页会自动列出 Tailscale 和局域网地址，无需手动执行此命令。

### 2. 配置 Windows

1. 从 [Releases](https://github.com/Ryan119911/TypeBridge/releases/latest) 下载 `TypeBridge.exe`。
2. 双击运行，不需要安装。
3. 第一次启动会自动打开设置窗口；填写**你自己的 Ubuntu 地址和端口**并保存。
4. 也可以右键托盘图标，选择“设置...”随时修改地址、端口和快捷键。

Windows 端配置文件为本机 `typebridge.ini`：

```ini
ip=100.x.y.z
port=4321
keyboard_hotkey=Ctrl+Alt+U
mouse_hotkey=Ctrl+Alt+M
mouse_with_keyboard=1
```

- `keyboard_hotkey`：键盘切换快捷键，也是联动模式下的总切换键。
- `mouse_hotkey`：取消联动后，单独切换鼠标的快捷键。
- `mouse_with_keyboard`：`1` 表示键盘和鼠标一起切换；`0` 表示分别切换。

该文件不会提交到 Git 仓库；也可以直接在设置窗口里改，保存后立即生效。快捷键在设置窗口中点击输入框、按下组合键即可录制，至少需要包含 Ctrl、Alt 或 Shift 之一。

### 3. 选择切换方式

**默认：键盘 + 鼠标一起切换**

1. 在 Ubuntu 中打开任意可编辑窗口并保持焦点。
2. 在 Windows 中按 `Ctrl + Alt + U`，键盘和鼠标同时交给 Ubuntu。
3. 输入拼音，用数字键或空格选字；鼠标可以直接点候选、选择文本。
4. 再按一次 `Ctrl + Alt + U`，键盘和鼠标一起回到 Windows。

**可选：键盘和鼠标分开切换**

在设置窗口取消勾选“键盘和鼠标一起切换”：

- `Ctrl + Alt + U` 只切换键盘；
- `Ctrl + Alt + M` 只切换鼠标；
- 托盘菜单会分别显示“键盘切换到 …”和“鼠标切换到 …”，托盘提示同时显示两者当前在哪一侧。

这样可以让键盘在 Ubuntu 输入，鼠标继续操作 Windows，适合边看 Windows 资料边在 Ubuntu 输入的场景。两个快捷键都可以在设置里改，但不能相同。

程序不会在 Windows 中打开微信输入法进行组词，也不会显示常驻输入窗口。

## 按键支持

| Windows 输入 | Ubuntu 行为 |
|---|---|
| 字母、数字、常用标点 | 转为字符发送，交给 Ubuntu 输入法 |
| 空格 | 发送空格，用于选字上屏 |
| 退格 | `K BackSpace` |
| 回车 | `K Return` |
| 方向键 | `K Up / Down / Left / Right` |
| PageUp / PageDown | `K Prior / Next` |
| Esc | `K Escape` |
| Tab | `K Tab` |
| Insert | `K Insert` |
| F1–F24 | `K F1` … `K F24` |
| 独立 Ctrl / Shift / Alt / Win | 松开修饰键时补发单键，Ubuntu 能收到独立按键 |
| CapsLock | 客户端维护大写状态，随后字母以大写字形发送，不依赖 Ubuntu 端 CapsLock 状态 |
| NumLock / ScrollLock | 注入锁定键，并保持对应锁定状态 |
| PrintScreen / Pause / Menu | 注入对应 X11 按键 |
| 小键盘 0–9 与运算符 | 注入数字和 `+ - * / .`，不会改变 NumLock 状态 |
| 音量、播放/暂停、上一曲/下一曲、停止 | 注入对应媒体键 |
| Ctrl/Alt/Win + 字母、数字、功能键 | `K ctrl+a`、`K alt+b`、`K super+e` 等组合 |
| 切换快捷键（默认 `Ctrl + Alt + U`） | 仅切换模式，不发送到 Ubuntu |

在 Ubuntu 模式下，所有被转发的按键都不会再进入 Windows 当前软件；单独的 Ctrl、Alt、Win 也会在松开时发送到 Ubuntu。

## 鼠标支持

鼠标接管是可选的：

- **联动模式（默认）**：键盘和鼠标一起切换，按一次快捷键两端都过去，再按一次都回来。
- **分开模式**：键盘用一个快捷键、鼠标用另一个快捷键，可以只把鼠标送到 Ubuntu，键盘仍留在 Windows。

鼠标被接管后，移动指针、点击、拖拽、滚轮都会送到 Ubuntu 当前指针位置，Windows 本机不再响应；切回 Windows 后鼠标立刻恢复，并回到接管前的位置。

| Windows 动作 | Ubuntu 行为 |
|---|---|
| 移动鼠标 | `M dx dy` → `xdotool mousemove_relative`（相对位移，与分辨率无关） |
| 左键 / 中键 / 右键 | `B 1/2/3 down\|up` → `mousedown` / `mouseup`，按下与松开分离所以支持拖拽 |
| 侧键（后退 / 前进） | `B 8/9` |
| 滚轮上下 | `W up / down` → `xdotool click 4/5` |
| 滚轮左右 | `W left / right` → `xdotool click 6/7` |

实现要点：

- 客户端每 **4ms** 合并一次位移（250Hz），避免高频鼠标事件把每个像素都发成一个 TCP 包。
  默认频率可在 `typebridge.ini` 里用 `mouse_hz` 调整（50–1000），配合 1ms 定时器精度使用。
- 连接建立时关闭 Nagle（`NoDelay`），小包立即发送；否则位移会攒到 ACK 才走，手感发黏。
- 服务端用 **XTEST 直连注入**（`ctypes` 调 `libXtst`，约 1μs/次），而不是每条命令 fork 一次 `xdotool`（约 2.2ms/次）。
  实测 1000 次相对位移端到端 9ms，旧实现需要约 2.2 秒；`libXtst` 不可用时自动回退到 `xdotool`。
- 服务端会把一批里连续的 `M` 行合并成一次注入，但遇到 `B` / `W` / 文本前会先落位，保证"先移动后点击"的顺序。
- 光标在接管期间被钉在屏幕中心，因此拖到屏幕边缘也不会丢位移；系统不会把指针“夹”在边上。
- 位移按屏幕物理像素 1:1 转发；进程启动时声明 DPI 感知，保证钩子坐标与光标坐标同一坐标系。
- 切回 Windows 时会把仍按下的键补发为 `up`，Ubuntu 服务端也会在断线时主动释放按钮，避免卡在拖拽状态。
- **断线自动放行**：连接断开时钩子不再拦截鼠标，本机鼠标立即恢复，不会把用户锁死。
- 单次位移上限 ±2000px，防止异常事件把指针甩飞。

## 安全说明

TypeBridge 的线路协议是**明文 TCP，没有内置认证或 TLS**。安全边界依赖网络层：

- 推荐只在 Tailscale / WireGuard 等加密私有网络中使用。
- 或在可信局域网中使用，并通过防火墙限制 4321 端口。
- **不要**在公网路由器上直接端口转发 4321。
- 任何能连接 4321 端口的人都能向 Ubuntu 当前焦点窗口注入按键，**也能移动指针和点击**。

Windows 端使用低级键盘与鼠标钩子：

- 只在 Ubuntu 模式下拦截并转发按键。
- Windows 模式下所有按键放行，不记录输入内容。
- 程序不会持久化按键日志。
- 部分安全软件会把全局键盘钩子标记为敏感行为，这是功能实现所必需的。

## 线路协议

协议为 UTF-8、按行分隔，每行以 `\n` 结尾。

| 行 | 含义 |
|---|---|
| `T <JSON字符串>` | 注入文本；JSON 字符串中的引号、反斜杠和换行必须正确转义 |
| `K BackSpace` | 退格 |
| `K Return` | 回车 |
| `K Up / Down / Left / Right` | 方向键 |
| `K Prior / Next` | PageUp / PageDown |
| `K Escape / Tab / Insert` | Esc、Tab、Insert |
| `K Space` | 空格，大小写键名均可 |
| `K F1` … `K F24` | 功能键 |
| `K ctrl / shift / alt / super` | 独立修饰键 |
| `K CapsLock / NumLock / ScrollLock` | 锁定键 |
| `K PrintScreen / Pause / Menu` | 打印屏幕、暂停、菜单键 |
| `K KP_0` … `K KP_9` | 小键盘数字，映射为普通数字，避免改变 NumLock |
| `K VolumeUp / VolumeDown / VolumeMute` | 音量键 |
| `K ctrl+a` / `K alt+b` / `K super+e` | 组合键，支持 `ctrl / alt / shift / super` |
| `M <dx> <dy>` | 相对移动指针，单位为像素，可为负；单次上限 ±2000 |
| `B <按钮> down\|up` | 按下/松开鼠标按钮，1 左 2 中 3 右 8 后退 9 前进 |
| `W up / down / left / right` | 滚轮上下左右，对应 X11 按钮 4/5/6/7 |
| `PING` | 连通性测试，返回 `PONG` |
| `QUIT` | 断开连接 |

`K` 键名和 `B` 按钮编号都使用白名单校验，未知值会被拒绝并记录日志。旧客户端只发送 `BackSpace` 和 `Return` 时仍然兼容。

非 ASCII 的 `T` 文本会由 Ubuntu 端写入剪贴板后粘贴，可能覆盖当前剪贴板内容。

## 从源码构建

### GitHub Actions

仓库自带 `.github/workflows/build.yml`。每次推送到 `main` 会生成 Windows EXE 和 Ubuntu DEB 构建产物；推送 `v*` 标签会自动创建 Release 并附带 `TypeBridge.exe` 与 `typebridge_<版本>_all.deb`。

### Windows 本地构建

要求 Windows 自带 .NET Framework 4.x，不需要安装 Visual Studio。

```text
双击 build.bat
```

或手动执行：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
  /out:"TypeBridge.exe" `
  /reference:System.dll /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
  "TypeBridge.cs"
```

`/target:winexe` 保证运行时不出现控制台窗口。

### Ubuntu DEB 本地打包

在 Ubuntu 上执行：

```bash
VERSION=3.2.1 bash ubuntu/build-deb.sh
```

生成：

```text
dist/typebridge_3.3.0_all.deb
```

安装包包含 GTK 设置页、Ubuntu 服务端、桌面菜单入口和 systemd 用户服务。

### PowerShell 旧版（仅键盘）

仓库保留 `TypeBridge.ps1` 和 `启动输入桥.vbs`，用于不能运行 EXE 的应急场景。注意它停留在早期形态：**只转发键盘、快捷键固定为 `Ctrl+Alt+U`、不读取新的快捷键和鼠标配置**。需要鼠标转发、可配置快捷键和 Ubuntu 图形安装包时，请使用 EXE + `.deb`。

## 目录结构

```text
TypeBridge/
├─ TypeBridge.cs                 Windows 客户端源码
├─ TypeBridge.ps1                PowerShell 备用实现
├─ typebridge-ubuntu.py          Ubuntu 服务端
├─ assets/                       应用图标 SVG / PNG / ICO
├─ ubuntu/
│  ├─ typebridge-ui.py           Ubuntu GTK 设置页
│  ├─ typebridge-ui              应用启动器
│  ├─ typebridge.desktop         应用菜单入口
│  ├─ typebridge.service         systemd 用户服务
│  └─ build-deb.sh               DEB 打包脚本
├─ build.bat                     本地 EXE 构建脚本
├─ typebridge.example.ini        Windows 配置模板
├─ 启动输入桥.vbs                 无终端启动 PowerShell 版
├─ 启动输入桥.bat                 备用启动器
├─ .github/workflows/build.yml   GitHub Actions 构建/Release
└─ README.md
```

`typebridge.ini`、`*.exe`、`*.deb`、`dist/`、`__pycache__/` 等本机文件已加入 `.gitignore`。

## 常见问题

### 托盘显示未连接

检查 Ubuntu 服务是否运行：

```bash
systemctl --user status typebridge --no-pager
```

检查端口和网络：

```bash
ss -lntp | grep 4321
tailscale status
```

Windows 端设置里确认 IP 和端口正确，并确认 Ubuntu 防火墙允许该端口。

### 连接成功但按键没有出现

确认 Ubuntu 当前窗口有文本焦点，且是 X11 会话。Wayland 下 xdotool 可能不生效。

### 中文没有候选窗口

确认 Ubuntu 输入法已启用，并且 `~/.config/typebridge/config.json` 中：

```json
"auto_deactivate_fcitx": false
```

修改后重启：

```bash
systemctl --user restart typebridge
```

### 切换快捷键无反应

快捷键可能被其他软件占用，或者设置里改成了别的组合。右键托盘图标可以直接切换，也可以打开“设置...”重新录制快捷键并查看是否提示“已被其他程序占用”。如果仍然无效，检查安全软件是否拦截了全局键盘钩子。

### 鼠标不动或点击没反应

- 默认联动模式下，连接成功才能接管鼠标；托盘提示“未连接”时鼠标会留在 Windows。
- 分开模式下请确认按的是鼠标快捷键，托盘会显示“鼠标:Ubuntu”或“鼠标:Windows”。
- 接管期间 Windows 本机鼠标会被锁定，这是正常现象；按对应快捷键即可恢复。

### 启动 EXE 后只有托盘，没有主窗口

这是预期行为。TypeBridge 没有常驻输入窗口；设置窗口只在首次运行或手动打开设置时出现。

### PowerShell 版报编码错误

`TypeBridge.ps1` 必须保存为 UTF-8 with BOM。仓库当前文件已带 BOM。

## 已知限制

- 仅支持 X11；Wayland 原生会话未验证。
- 不传输画面，不支持远程桌面场景。
- 鼠标按相对位移转发，两端分辨率或缩放不同也能用；但没有绝对坐标定位，无法把指针"跳"到 Ubuntu 的某个固定位置。
- 依赖 Ubuntu 输入法本身；TypeBridge 不提供候选词界面。
- 部分全屏游戏或使用原始输入的程序可能不兼容。
- `Ctrl + Alt + U` 和 `Ctrl + Alt + M` 是默认快捷键，可在设置中修改；`Ctrl + Alt + Delete` 由操作系统保留，无法转发。
- 键盘厂商自定义的额外按键可能没有标准 X11 键名。
- 明文 TCP 协议，必须配合 Tailscale、可信局域网和防火墙使用。
- 切换快捷键属于保留组合，按下时只切换模式，不会发送到 Ubuntu。
