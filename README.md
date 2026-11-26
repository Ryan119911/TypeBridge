# TypeBridge

<img src="assets/typebridge-128.png" width="96" alt="TypeBridge icon">

**一套键盘鼠标，同时操控 Windows 和 Ubuntu。**

TypeBridge 让 Windows 上的输入直接进入 Ubuntu 当前窗口：键盘、鼠标、剪贴板、文件全部打通，随时用一个键在两台机器之间来回切。它只转发输入、不传画面，所以几乎不占带宽；中文由 **Ubuntu 自己的输入法**组词选字，候选窗口和词库都还是你熟悉的那一套。

反过来也一样：坐在 Ubuntu 前面时按一个键（默认 `Ctrl + Alt + U`），共享就调个头，用 Ubuntu 的键鼠操作 Windows——两台机器谁都能当主机。

[![Build TypeBridge](https://github.com/Ryan119911/TypeBridge/actions/workflows/build.yml/badge.svg)](https://github.com/Ryan119911/TypeBridge/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/Ryan119911/TypeBridge)](https://github.com/Ryan119911/TypeBridge/releases)

## 适合谁用

- 两台机器（Windows + Ubuntu）摆在一起，想用一套键鼠操控，桌面不用摆两个键盘。
- 需要频繁在两边复制粘贴、传文件，但不想每次插 U 盘或走网盘。
- 要在 Ubuntu 里正常打中文，又不想在 Windows 上再装一套输入法。
- 不想上远程桌面：画面传输吃带宽、缩放和延迟都别扭，而这里只传输入事件。

不适合：需要看到 Ubuntu 画面的场景（本程序不传画面）、Wayland 会话、公网直连。

## 功能

### 键盘

- **一键切换**：全局快捷键（默认 `Ctrl + Alt + U`），在任何软件里都能按，不用先切窗口。
- **完整转发**：字母数字与标点、方向键、F1–F12、小键盘、Home/End/PageUp/PageDown、Insert/Delete、媒体键（音量、播放、上一曲），以及 `Ctrl+A`、`Alt+Tab`、`Win+E` 这类组合键。
- **修饰键单独可用**：Ctrl / Shift / Alt / Win 单独按下也会送到 Ubuntu。
- **大写状态跟随 Windows**：CapsLock 打开时发过去就是大写字母，不会因为两端状态不同打错。
- **输入法还是 Ubuntu 的**：拼音字母、数字、空格发往 Ubuntu，候选窗口由 Ubuntu 输入法弹出，可以按数字选字，也能用鼠标点候选。
- **不漏字**：Ubuntu 模式下被转发的按键不会同时进入 Windows 的当前软件；切回 Windows 立刻恢复本机输入。
- **安全兜底**：连接断开或对端无响应时自动把键鼠还给 Windows，不会让人卡在另一端。

### 鼠标

- **完整接管**：移动、左/中/右键、侧键（前进/后退）、滚轮上下左右都送到 Ubuntu，拖拽、框选、拖放手感与本地一致。
- **分辨率无关**：按位移转发而不是跳坐标，两端分辨率、缩放比例不同也能用。
- **光标不碍眼**：接管期间 Windows 上的光标会隐藏（否则它会僵在屏幕中间），切回来立刻恢复并回到原位置。
- **对端指针不藏**：控制权在 Windows 时，Ubuntu 的指针照常显示——人走到 Ubuntu 前面随时能接着用（3.7.3 起不再自动隐藏，之前会一直看不见）。

### 双向共享（Ubuntu → Windows）

- **调头就用**：在 Ubuntu 上按 `Ctrl + Alt + U`，它的键盘、鼠标、滚轮、侧键就转到 Windows，Windows 上的光标跟着动、点哪算哪。
- **推边缘就过去**：把 Ubuntu 的指针推到共享边就过渡到 Windows——和 Windows 那边推边缘完全一致，按位置判定，不看推得快慢。
- **随时抢得回来**：Windows 正把键鼠送到 Ubuntu 时，直接搬一下 Ubuntu 的鼠标就把控制权拿回来（不用推边缘，也不会因为指针停在边上而误抢）。
- **在 Windows 这边也能收回**：Ubuntu 正驱动本机时，碰一下 Windows 的鼠标或键盘，控制权立刻回来——对端没主动交还时也不会把本机锁在接管态。
- **回来的路**：再按一次 `Ctrl + Alt + U`；或把 Windows 上的光标顶到共享边继续外推。Windows 端按的也是同一个键——坐在哪边，就把它交给另一边。
- **不会互抢**：一边正在驱动另一边时，另一边的接管请求会被拒绝，两个方向永远只有一个生效。
- **断了自己回来**：连接断了、心跳没了、对端没确认，Ubuntu 都会立刻放开抓取，不会把你锁在远端。

### 屏幕边缘切换

- **像硬件 KVM**：鼠标推出指定边就过去，从共享边推回就回来，不用按快捷键。
- **推到边上就切**：按位置判定，光标落到共享边这一拍就切，不看推得快慢，也不要求停在边上等。
- **排列可拖动**：设置页里按住 Ubuntu 方块拖到本机的任意位置——上、下、左、右以及四个斜角都行，靠近整齐位置会自动吸附，松手生效，和 Windows 的显示器排列一个手感。
- **可关闭**：不想用就关掉，只保留快捷键切换。

### 剪贴板

- **双向自动同步**：Windows 复制、Ubuntu 粘贴（或反过来），文本和图片都支持，不用按任何键。
- **截图直接过**：Windows 截图工具、浏览器「复制图片」、Excel 复制区域，都能在另一端直接粘贴；图和文本会一起带过去，粘到哪里由目标程序自己挑。

### 文件互传

- **Windows → Ubuntu**：按住左键把文件拖到屏幕边缘的传送区，或从托盘菜单选「发送文件…」。传完会提示文件落在 Ubuntu 的哪个目录。
- **Ubuntu → Windows**：把文件丢进桌面的「发送到Windows」文件夹，自动传到 Windows 的接收目录（默认 `下载\typebridge`）。
- **看得见进度**：右下角小面板显示文件名、百分比、已传/总大小、实时速度和剩余时间，中途可以取消。
- **断了能续**：网络中断、手动取消、机器重启之后，重发同一个文件会从断点继续，不用重头再来。
- **不打断打字**：传输在后台进行，键盘鼠标照常用。
- **命令行**：`TypeBridge.exe --send "D:\big.iso"`，可一次跟多个文件，适合脚本和计划任务。

### 托盘与设置

- 托盘图标显示当前状态（在哪一侧、是否连接、往返延迟），右键可切换、发送文件、打开设置。
- 设置窗口可改地址、端口、快捷键、屏幕排列、文件目录，保存即生效。
- 键盘与鼠标可**联动**（一个键一起切），也可**分开**（两个快捷键各管一边，适合鼠标在 Windows 查资料、键盘在 Ubuntu 输入）。
- **安装版**：独立的 `TypeBridgeSetup.exe` 会把程序装到固定目录并自动注册开始菜单，桌面快捷方式和开机自启可在安装时勾选；装好后也能在设置页「开机与快捷方式」里随时改。

## 安装

### 下载

- Windows 安装版：`TypeBridgeSetup.exe`（推荐，自动注册开始菜单）
- Windows 免安装：`TypeBridge.exe`（单文件直接运行）
- Ubuntu：`typebridge_<版本>_all.deb`
- 最新版本：<https://github.com/Ryan119911/TypeBridge/releases/latest>

每台 Windows 都只连接**你自己填写的 Ubuntu**，项目没有中央服务器。

### Ubuntu 端

1. 下载 `.deb`，双击安装（输入一次管理员密码）。
2. 从应用菜单打开 **TypeBridge**，页面上会显示本机 IP 和端口，把它填到 Windows 端。
3. 完成，不需要敲命令。

这个页面还能启停/重启服务、改端口、复制地址、通过系统授权窗口放行防火墙。

> 装完如果在应用列表里没看到图标，注销重新登录一次就出现了（服务已设为开机自启，安装脚本也会尽力帮当前用户启动一次）。图标是系统级安装的，机器上所有用户都能看到。

**要求**：Ubuntu 22.04 及以上、**X11 会话**（Wayland 下不可用），已启用输入法（fcitx 或 ibus）。

<details>
<summary>手动安装</summary>

```bash
sudo apt install -y python3-gi gir1.2-gtk-3.0 xdotool
install -Dm755 typebridge-ubuntu.py ~/.local/bin/typebridge.py
install -m644 typebridge_protocol.py typebridge_files.py ~/.local/bin/
```

`~/.config/systemd/user/typebridge.service`：

```ini
[Unit]
Description=TypeBridge - Windows to Ubuntu input bridge
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

用 ibus 就把三处 `fcitx` 换掉。`XAUTHORITY` **不能照抄**，它随登录管理器而变：gdm/GNOME 是 `%t/gdm/Xauthority`，LightDM / SDDM 一般是 `%h/.Xauthority`，Wayland 会话里还有 `%t/.mutter-Xwaylandauth.*`。填错的表现是服务在跑但键盘注入不了。`.deb` 安装的那份用包装脚本自动挑，手动安装时自己确认一下：

```bash
ls -l /run/user/$(id -u)/gdm/Xauthority ~/.Xauthority 2>/dev/null
```

```bash
systemctl --user daemon-reload
systemctl --user enable --now typebridge
sudo ufw allow 4321:4323/tcp     # 只在可信网络里放行
```

Ubuntu 端配置在 `~/.config/typebridge/config.json`：

```json
{
  "port": 4321,
  "auto_deactivate_fcitx": false,
  "reverse_share": true,
  "hotkey": "ctrl+alt+u",
  "edge_entry": true,
  "file_send_host": ""
}
```

`auto_deactivate_fcitx` 必须保持 `false`，否则拼音进不了输入法。

`reverse_share` 关掉后不再注册全局热键、也不做边缘进入（托盘菜单里的接管仍可用）；`hotkey` 是反向切换键，写法与按键协议一致（`ctrl+alt+u`、`super+grave`，也可以单独用一个 `ScrollLock`）；`edge_entry` 控制"指针顶到共享边停半秒就切过去"。改完 `systemctl --user restart typebridge` 生效。

`file_send_host` 用于指定 Ubuntu 向 Windows 发送文件和剪贴板图片的目标地址。默认留空，跟随已确认的 Windows 控制连接；探测连接不会改变目标，控制连接断开后暂停发送。如果键鼠已连接、文件却因多网卡或代理路由无法返回，可填 Ubuntu 能访问的 **Windows 地址**（例如 Windows 的 Tailscale IP，不带端口），并确保 Windows 文件接收端口（默认 `4323`）可达。修改后重启上述用户服务生效。

</details>

### Windows 端

两种用法选一种：

1. **安装版（推荐）**：下载 `TypeBridgeSetup.exe`，双击运行，按提示选择安装位置和选项。开始菜单快捷方式会自动注册。
2. **免安装**：下载 `TypeBridge.exe`，双击直接运行。

第一次启动都会弹出设置窗口，填**你自己的 Ubuntu 地址和端口**；之后右键托盘图标 →「设置...」可随时修改。

依赖只有系统自带的 .NET Framework 4.x，不需要额外安装运行时。

如果 Windows Terminal 或其他目标程序以管理员权限运行，请在 TypeBridge 托盘菜单选择「以管理员权限重启（兼容管理员终端）」并确认 Windows 授权提示。托盘显示「管理员模式已开启」后，再使用热键或屏幕边缘切换。Windows 的权限隔离会限制普通权限程序对管理员窗口的输入操作。

### Windows 安装版

`TypeBridgeSetup.exe` 是独立安装程序，主程序已经打包在里面，不需要和 `TypeBridge.exe` 放在一起。

安装时可以选择：

- **安装位置**：默认 `%LOCALAPPDATA%\Programs\TypeBridge`，只装给当前用户，不需要管理员权限；
- **开机自动启动**：登录 Windows 后自动在托盘运行，不用每次手动打开；
- **创建桌面快捷方式**：在桌面放一个 TypeBridge 图标。

**开始菜单快捷方式在安装时自动注册**，之后在开始菜单搜索 TypeBridge 就能打开，也会出现在「设置 → 应用」的已安装列表里。

重装或升级会覆盖程序文件，但**保留安装目录里已有的 `typebridge.ini`**，地址和快捷键不会丢。安装包旁边如果放了 `typebridge.ini`，首次安装会一并复制过去，适合批量部署。

装好之后，设置页最下面的「开机与快捷方式」卡片可以随时开关开机自启、桌面快捷方式和开始菜单快捷方式。免安装运行时这张卡片管理的是当前 exe 的集成状态；想要固定位置和开始菜单，用安装版。

卸载：托盘右键 →「卸载 TypeBridge...」，或 Windows「设置 → 应用 → 已安装的应用」里找到 TypeBridge。卸载会删除开始菜单/桌面快捷方式、开机启动项，以及安装目录中的程序文件。

### 命令行

| 命令 | 作用 |
|---|---|
| `TypeBridgeSetup.exe` | 安装到本机（独立安装程序） |
| `TypeBridge.exe --uninstall` | 卸载已安装的副本（Windows「设置 → 应用」用的也是它） |
| `TypeBridge.exe --send "D:\big.iso"` | 直接发送文件，可一次跟多个 |

`--autostart` 由开机启动项自动带上，手动运行不需要。

### 两边怎么连

推荐 Tailscale（自带加密和 NAT 穿透），也可以直接用局域网 IP：

```bash
tailscale ip -4          # 例如 100.x.y.z
ip -4 addr | grep inet   # 局域网地址
```

地址支持填多个，用逗号分隔，程序会**自动挑延迟最低的那条**，适合同时开着代理 / TUN 的情况：

```ini
ip=100.64.0.10, 192.168.1.20
```

## 使用

### 切换键盘鼠标

**联动（默认）**：按一次 `Ctrl + Alt + U`，键盘和鼠标一起交给 Ubuntu；再按一次一起回来。

**分开**：在设置里取消「键盘和鼠标一起切换」，此后

- `Ctrl + Alt + U` 只切键盘，
- `Ctrl + Alt + M` 只切鼠标。

适合「鼠标留在 Windows 查资料、键盘去 Ubuntu 输入」的用法。两个快捷键都能改，但不能相同。

### 反过来：用 Ubuntu 的键鼠操作 Windows

坐在 Ubuntu 前面时不用碰 Windows 的键盘：

- 按 `Ctrl + Alt + U`（Ubuntu 端的全局热键，可在 `~/.config/typebridge/config.json` 里改），Windows 上的光标立刻出现，键盘输入的字符直接打进 Windows 当前窗口。
- 或者把指针推到共享边，直接过渡过去；Windows 正在驱动 Ubuntu 时，搬一下 Ubuntu 的鼠标就能拿回控制权。
- 回来：再按一次 `Ctrl + Alt + U`，或者把 Windows 上的光标顶到共享边继续外推；也可以直接在 Windows 上按 `Ctrl + Alt + U` 收回。

Windows 设置页里的「双向共享」开关（ini 里的 `reverse_enable`）可以整体关掉这个方向。两边同时只有一个方向生效。

> 两边默认用**同一个组合键** `Ctrl + Alt + U`：坐在哪台机器前，按一下就把键鼠交给另一台。别用 `Ctrl + Alt + W`——那是微信的全局热键，按下先弹微信。Ubuntu 端要换键，改 `~/.config/typebridge/config.json` 的 `hotkey`。

### 摆放屏幕

打开设置 →「屏幕排列」，按住 Ubuntu 方块拖到本机的任意位置：上、下、左、右或四个斜角都能放，靠近整齐位置会自动吸附，松手生效。斜角摆放时，切屏方向按偏移更大的那个方向算（例如放在右上，从右边缘进入）。

摆好之后，鼠标推出对应的那条边就进入 Ubuntu，从那侧推回来就回到 Windows。

同一个卡片下面有「启用边缘切屏」开关，关掉后边缘不再触发切换（方块会变灰），只能用快捷键切；对应 ini 里的 `edge_switch`。注意这个开关是总开关，位置本身仍由拖动决定。

### 传文件

- **发到 Ubuntu**：按住左键把文件拖到屏幕边缘的传送区，或托盘菜单「发送文件…」。
- **发到 Windows**：把文件放进 Ubuntu 桌面的「发送到Windows」文件夹。
- 进度面板可以取消；取消或失败后重发同一文件会接着传。

## 配置项

Windows 端配置在 EXE 同目录的 `typebridge.ini`，设置窗口保存时会重写该文件。

| 键 | 默认 | 说明 |
|---|---|---|
| `ip` | `127.0.0.1` | Ubuntu 地址，可写多个（逗号分隔），自动挑延迟最低的 |
| `port` | `4321` | 控制端口 |
| `keyboard_hotkey` | `Ctrl+Alt+U` | 键盘切换键，也是联动模式的总切换键 |
| `mouse_hotkey` | `Ctrl+Alt+M` | 分开模式下单独切鼠标 |
| `mouse_with_keyboard` | `1` | `1` 联动切换；`0` 键盘鼠标分开 |
| `edge_switch` | `1` | `1` 启用屏幕边缘切换；`0` 关闭 |
| `switch_edge` | `right` | 切屏方向：`left` / `right` / `top` / `bottom`（设置里拖动即可改） |
| `layout_offset` | 空 | 方块相对本机的摆放偏移（如 `150,-100`），由设置页拖动生成，一般不用手改 |
| `switch_toast` | `0` | `1` 切换时在右下角弹提示 |
| `file_port` | `4322` | Windows → Ubuntu 的文件端口 |
| `file_recv_port` | `4323` | Ubuntu → Windows 的文件端口 |
| `recv_dir` | 空 | Windows 接收目录，空表示 `下载\typebridge` |
| `remote_dir` | 空 | Ubuntu 接收目录，空表示 Ubuntu 桌面 |
| `reverse_enable` | `1` | `1` 允许 Ubuntu 反向控制本机；`0` 一律拒绝它的接管请求 |
| `mouse_hz` | `250` | 鼠标移动上报频率（50–1000）。手写项：改完不要再在设置里点保存 |

**快捷键怎么设**：在设置里点输入框、直接按下想要的组合键即可，不需要记写法。必须带 Ctrl / Alt / Shift 之一；想用一个不会和任何软件冲突的整键，可以单独用 `ScrollLock`、`Pause` 或 `Menu`（在设置里按对应键即可）。

开机自启和快捷方式属于 Windows 系统状态（`HKCU\...\Run` 与开始菜单/桌面的 `.lnk`），不写进 `typebridge.ini`；在设置页「开机与快捷方式」里改即可。

## 安全提示

TypeBridge 走**明文 TCP，没有内置认证或加密**，安全边界完全依赖网络环境：

- 推荐只在 Tailscale / WireGuard 这类加密私有网络里使用。
- 或在可信局域网里使用，并用防火墙限制 4321–4323 端口。
- **不要**在公网路由上做端口转发。
- 能连上 4321 的人可以向 Ubuntu 当前窗口注入按键、移动指针和点击；能连上 4323 的人可以给你投放文件（文件名已做穿越与非法字符过滤）。

Windows 端使用全局键盘/鼠标钩子：只在 Ubuntu 模式下拦截并转发，Windows 模式下全部放行，不记录、不保存输入内容。部分安全软件会把全局钩子标记为敏感行为，这是功能实现所必需的。

## 已知限制

- 仅支持 X11；Wayland 会话不可用。
- 不传画面，不能当远程桌面用，也看不到 Ubuntu 屏幕。
- 鼠标按相对位移转发，无法把指针直接「跳」到 Ubuntu 的某个绝对坐标。
- 不提供候选词界面，中文完全依赖 Ubuntu 输入法。
- 独占全屏的游戏、走原始输入（Raw Input）的程序可能收不到转发。
- 键盘厂商自定义的额外按键可能没有对应的 X11 键名。
- 边缘切换只有四条边，不支持斜角摆放。
- 反向共享用 `SendInput` 注入：Windows 上以管理员权限运行的窗口（UAC 提权）收不到这些按键和点击。
- 反向共享时中文要用 **Windows 自己的输入法**——Ubuntu 的候选窗口在另一块屏幕上，看不见。
- 连接是单条 TCP，反向共享要求 Ubuntu 端是 3.7 或更高版本；旧版 Ubuntu 端收到接管请求不会回应，Windows 会拒绝。
- 文件传输是单条连接，速度由网络决定，1 GB 大约 2–3 分钟。
- 明文协议，必须配合 Tailscale 或可信局域网 + 防火墙。

## 从源码构建

推 `main` 由 GitHub Actions 产出 EXE 与 DEB；推 `v*` 标签自动创建 Release。

本地构建：Windows 双击 `build.bat`（用系统自带 .NET Framework，不需要 Visual Studio），会同时产出 `TypeBridge.exe` 和 `TypeBridgeSetup.exe`（安装器把主程序内嵌在里面）；Ubuntu 上 `VERSION=<版本> bash ubuntu/build-deb.sh`，产物在 `dist/`。

Windows 回归测试运行 `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Test`。
鼠标回调回归测试运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Run-DesktopChecks.ps1 -Executable ./TypeBridge.Tests.exe -Argument --clipboard-sync`，在独立的 Windows 桌面中验证程序移动不会误收回控制、真实移动和点击仍可收回。
Ubuntu 回归测试运行 `python3 tests/run_linux.py`，需要 `xvfb`、`x11-utils` 以及正常运行所需的 GTK 和 xdotool。测试在独立的 X11 显示 `:97` 中运行。

Windows 的连接、剪贴板、文件、反向控制和输入钩子分别放在 `BridgeTransport.cs`、`BridgeClipboard.cs`、`BridgeFiles.cs`、`BridgeReverse.cs` 和 `InputHooks.cs`。`ControlProtocol.cs` 管理分帧与有界发送队列。Ubuntu 的文件传输和协议缓冲位于 `typebridge_files.py` 与 `typebridge_protocol.py`。这些模块使用系统运行时和标准库。
