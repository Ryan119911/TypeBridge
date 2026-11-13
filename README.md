# TypeBridge

<img src="assets/typebridge-128.png" width="96" alt="TypeBridge icon">

**在 Windows 上直接打字，输入实时进入 Ubuntu 当前焦点窗口；鼠标、剪贴板、文件也一起打通。**

TypeBridge 是一个常驻托盘的 Windows 输入桥：只转发输入，不传画面。中文不在 Windows 端提交，而是把拼音和候选选择键送到 Ubuntu，由 **Ubuntu 自己的输入法**组词选字，所以候选窗口、词库、自造词都还是你在 Ubuntu 上习惯的那一套。

[![Build TypeBridge](https://github.com/Ryan119911/TypeBridge/actions/workflows/build.yml/badge.svg)](https://github.com/Ryan119911/TypeBridge/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/Ryan119911/TypeBridge)](https://github.com/Ryan119911/TypeBridge/releases)

## 适用场景

- 两台机器（Windows + Ubuntu）摆在一起，想用**一套键鼠**操控。
- 不想上远程桌面：画面传输吃带宽、缩放和剪贴板都别扭，而这里只传几十字节的输入事件。
- 需要在 Ubuntu 里正常打中文——拼音由 Ubuntu 的 fcitx/ibus 接管，不是把成品汉字硬塞进去。

不适用于：需要看到 Ubuntu 画面的场景（本程序不传画面）、Wayland 原生会话、公网直连。

## 特性

**输入**

- 全局快捷键切换（默认 `Ctrl + Alt + U`），键盘与鼠标可联动也可分开。
- 全键盘转发：字母数字标点、方向键、功能键 F1–F12、小键盘、媒体键、修饰键、组合键（`Ctrl+A` 等）。
- Ubuntu 模式下被转发的按键不会漏进 Windows；断开连接自动放行，不会把人锁死。

**鼠标与切屏**

- 鼠标接管后移动、左右中键、侧键、滚轮全部送到 Ubuntu，走相对位移，两端分辨率/缩放不同也能用。
- **屏幕边缘切换**：鼠标推出指定边就过去，从共享边推回就回来，像硬件 KVM。
- **屏幕排列可拖动**：设置里按住 Ubuntu 方块拖到本机的任意一侧，靠近自动吸附，松手生效——和 Windows 的显示器排列一个手感。
- **光标不打架**：接管期间 Windows 本机光标隐藏（钉在中心的动点不再露出来）；切回 Windows 后 Ubuntu 那边僵住的指针也会隐藏，而键盘还留在 Ubuntu 时它照常可用。

**剪贴板**

- 文本与图片双向自动同步。截屏、浏览器「复制图片」都能直接到另一端粘贴。
- 图和文本同时存在时一起带过去，目标程序粘哪个自己挑。

**文件**

- Windows 拖文件到边缘传送区（或托盘菜单）发往 Ubuntu；Ubuntu 把文件丢进桌面「发送到Windows」文件夹即回传。
- **实时进度**：右下角小面板显示文件名、百分比、速度、剩余时间，可中途取消。
- **断点续传**：中断、取消、关机后重传同一文件会从断点续写，不重头再来。
- 命令行：`TypeBridge.exe --send <文件>…`，方便脚本和计划任务。

**网络与运维**

- **自动选路**：`ip` 可填多个地址，每次连接并行探测、比较往返延迟，自动走最快的那条（TUN/代理把 Tailscale 拖慢时自动改走局域网）。
- 托盘提示带实时延迟；超过 120ms 会在日志里记 `network slow`。
- Ubuntu 端有顶栏图标（连接状态、一键切回 Windows、重启服务）和图形设置页（查看 IP、改端口、启停服务）。
- 单实例运行，重复启动会自动退出后面那个。

## 它是怎么工作的

```text
┌──────────────────────────────┐
│ Windows 10/11                │
│                              │
│  TypeBridge.exe              │
│    ├─ 托盘图标 / 设置窗口      │
│    ├─ 全局快捷键              │
│    └─ 低级键盘 / 鼠标钩子      │
└──────────────┬───────────────┘
               │ TCP 4321（控制 + 输入事件）
               │ 4322 / 4323（文件）
               │ Tailscale / 局域网
               ▼
┌──────────────────────────────┐
│ Ubuntu 22.04+（X11）          │
│                              │
│  typebridge.py               │
│    ├─ 行协议服务器            │
│    ├─ XTEST 注入按键/指针     │
│    └─ 交给 Ubuntu 输入法组词   │
└──────────────┬───────────────┘
               ▼
       Ubuntu 当前焦点窗口
```

| 端口 | 方向 | 用途 |
|---|---|---|
| 4321 | Windows → Ubuntu | 控制连接：输入事件、剪贴板文本、心跳 |
| 4322 | Windows → Ubuntu | 传文件到 Ubuntu |
| 4323 | Ubuntu → Windows | 传文件到 Windows（Windows 侧监听） |

两种输入模式：

| 模式 | 行为 |
|---|---|
| Windows | 钩子只观察不拦截，Windows 一切照旧 |
| Ubuntu | 拦截字符、特殊键、组合键并发往 Ubuntu，同时阻止它们进入 Windows |

模式、连接状态、往返延迟都显示在托盘提示里。

## 安装

### Ubuntu 端（推荐用 deb）

1. 从 [Releases](https://github.com/Ryan119911/TypeBridge/releases/latest) 下载 `typebridge_<版本>_all.deb`。
2. 双击安装（或在「软件安装」里打开），输入一次管理员密码。
3. 从应用菜单打开 **TypeBridge**，页面上会显示本机 IP、端口和服务状态。
4. 记得把 IP 填到 Windows 端。

这个页面还能：启停/重启服务、改端口、复制要填给 Windows 的地址、通过系统授权窗口放行 ufw 端口。日常不需要敲 `systemctl`。

**要求**：Ubuntu 22.04 及以上，**X11 会话**（Wayland 下注入不可靠）；已启用输入法（fcitx 或 ibus）。

<details>
<summary>手动安装（开发者用）</summary>

```bash
sudo apt install -y python3-gi gir1.2-gtk-3.0 xdotool
install -Dm755 typebridge-ubuntu.py ~/.local/bin/typebridge.py
mkdir -p ~/.config/systemd/user
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

用 ibus 就把三处 `fcitx` 换掉；`XAUTHORITY` 路径按实际会话调整。

```bash
systemctl --user daemon-reload
systemctl --user enable --now typebridge
systemctl --user status typebridge --no-pager
```

Ubuntu 端配置在 `~/.config/typebridge/config.json`：

```json
{
  "port": 4321,
  "auto_deactivate_fcitx": false
}
```

`auto_deactivate_fcitx` 必须是 `false`，否则发送前会关掉 fcitx，拼音进不了输入法。改完重启服务。

局域网访问需要放行端口（**只在可信网络里放行**）：

```bash
sudo ufw allow 4321/tcp
sudo ufw allow 4322/tcp
```

</details>

### Windows 端

1. 下载 `TypeBridge.exe`，双击运行，无需安装。
2. 首次启动会自动弹出设置窗口，填**你自己的 Ubuntu 地址和端口**。
3. 之后右键托盘图标 →「设置...」可随时修改。

依赖只有系统自带的 .NET Framework 4.x，不需要装 Visual Studio 或运行时。

### 两边怎么连

推荐 Tailscale（自带加密与 NAT 穿透），也可以直接用局域网 IP：

```bash
tailscale ip -4     # 例如 100.x.y.z
ip -4 addr | grep inet
```

地址可以直接填多个，用逗号分隔，程序会自动挑延迟最低的：

```ini
ip=100.64.0.10, 192.168.1.20
```

## 日常使用

### 切换键盘 / 鼠标

**联动（默认）**：按一次 `Ctrl + Alt + U`，键盘和鼠标一起交给 Ubuntu；再按一次一起回来。

**分开**：设置里取消「键盘和鼠标一起切换」，然后

- `Ctrl + Alt + U` 只切键盘，
- `Ctrl + Alt + M` 只切鼠标。

适合「鼠标在 Windows 上查资料、键盘在 Ubuntu 里输入」的用法。两个快捷键都能改，但不能相同。

### 屏幕排列与边缘切换

设置页的「屏幕排列」里，**按住 Ubuntu 方块拖到本机的任意一侧**（上、下、左、右都行），靠近某一侧会自动吸附，松手生效。切屏方向就按这个位置算：

- 放在右边 → 鼠标推到 Windows 右边缘进入 Ubuntu，从 Ubuntu 左边缘推回来；
- 放在上面/下面同理。

不想用边缘切换就把它关掉（`edge_switch=0`），只用快捷键。

光标规则：接管期间 Windows 光标隐藏；回到 Windows 后 Ubuntu 的指针隐藏；**键盘还留在 Ubuntu 时**（分开模式）Ubuntu 指针保持可见可用。

### 剪贴板

复制即同步，文本和图片都支持，不需要按任何键。图片走文件通道（`CLIPIMG`），几 MB 的截图不会把控制通道撑爆，也不落盘。

### 文件互传

- **Windows → Ubuntu**：拖文件到屏幕边缘的传送区（按住左键拖到边缘），或托盘菜单「发送文件…」。落地路径会在提示里回显，托盘菜单另有「在 Ubuntu 上打开接收目录」。
- **Ubuntu → Windows**：把文件丢进桌面的「发送到Windows」文件夹，自动传到 Windows 的接收目录（默认 `下载\typebridge`）。
- 传输时右下角有进度面板（文件名 / 百分比 / 速度 / 剩余时间 / 取消），上传在后台线程跑，不影响打字。
- 中断后重传同一文件会自动续传；`UPLOAD`/`RESUME` 协议细节见下文。
- 命令行：

```powershell
TypeBridge.exe --send "D:\big.iso"   # 可跟多个文件
```

读同目录 `typebridge.ini` 的地址与端口，不影响正在运行的托盘实例，退出码 0 表示全部成功。

## 配置参考

Windows 端配置在 EXE 同目录的 `typebridge.ini`（不会提交到仓库）。设置窗口保存时会重写这个文件。

| 键 | 默认 | 说明 |
|---|---|---|
| `ip` | `127.0.0.1` | Ubuntu 地址，可写多个（逗号/分号/空格分隔），连接时自动挑延迟最低的 |
| `port` | `4321` | 控制端口 |
| `keyboard_hotkey` | `Ctrl+Alt+U` | 键盘切换键，也是联动模式的总切换键 |
| `mouse_hotkey` | `Ctrl+Alt+M` | 分开模式下单独切鼠标的键 |
| `mouse_with_keyboard` | `1` | `1` 联动；`0` 分开 |
| `edge_switch` | `1` | `1` 启用边缘切换；`0` 关闭 |
| `switch_edge` | `right` | 边缘切换方向：`left` / `right` / `top` / `bottom`（设置里拖动即可改） |
| `switch_toast` | `0` | `1` 切换时弹右下角提示 |
| `file_port` | `4322` | Windows → Ubuntu 的文件端口 |
| `file_recv_port` | `4323` | Windows 侧接收文件用的端口（Ubuntu → Windows） |
| `recv_dir` | 空 | Windows 接收目录，空表示 `下载\typebridge` |
| `remote_dir` | 空 | 让 Ubuntu 用哪个目录接收，空表示 Ubuntu 桌面 |
| `mouse_hz` | `250` | 鼠标位移合并频率（50–1000）。手写项：设置窗口保存时会按已知字段重写文件，改完这一项不要再在设置里点保存 |

**快捷键规则**：在设置里点输入框、直接按下组合键即可录制。必须含 Ctrl/Alt/Shift 之一；想用「几乎不会冲突的整键」，可以单独用 `ScrollLock`、`Pause`、`Menu`（在设置里直接按对应键），或在 ini 里手写 `keyboard_hotkey=Win+Pause`。`Ctrl+Alt+Delete` 由系统保留，无法使用。

Ubuntu 端配置见前面的 `config.json`；端口、目录这类日常项在图形设置页里改更省事。`.deb` 安装后端口默认 4321/4322。

## 按键与鼠标映射

| Windows 输入 | Ubuntu 行为 |
|---|---|
| 字母、数字、常用标点 | 作为文本发送，交给 Ubuntu 输入法 |
| 空格 | 发送空格（选字上屏） |
| 退格 / 回车 / Tab / Esc / Insert | `K BackSpace` / `Return` / `Tab` / `Escape` / `Insert` |
| 方向键、PageUp/PageDown | `K Up`… / `K Prior`、`Next` |
| F1–F12 | `K F1`…`F12` |
| 独立 Ctrl / Shift / Alt / Win | 松开时补发单键 |
| CapsLock | 客户端自己维护大小写，字母以大写字形发送 |
| NumLock / ScrollLock | 注入锁定键并保持状态 |
| PrintScreen / Pause / Menu | 注入对应 X11 键 |
| 小键盘 0–9 与运算符 | 注入数字和 `+ - * / .`，不改 NumLock |
| 音量 / 播放 / 上一曲 / 下一曲 / 停止 | 注入媒体键 |
| Ctrl/Alt/Win + 字母数字功能键 | `K ctrl+a` 这类组合键 |

| Windows 鼠标动作 | Ubuntu 行为 |
|---|---|
| 移动 | `M dx dy`，相对位移，与分辨率无关 |
| 左/中/右键 | `B 1/2/3 down\|up`，按与松分离，支持拖拽 |
| 侧键 | `B 8/9` |
| 滚轮上下 / 左右 | `W up/down` / `W left/right` |

实现要点：

- 客户端每 4ms 合并一次位移（默认 250Hz），避免一个像素一个包；定时器只在接管且连接正常时运行，空闲不占 CPU。
- 连接建立即关闭 Nagle，小包立刻发出，位移不发黏。
- Ubuntu 端用 **XTEST 直连注入**（`ctypes` 调 `libXtst`，约 1μs 次），比每条命令 fork 一个 `xdotool`（约 2.2ms）快三个数量级；`libXtst` 缺失时自动回退。
- 服务端把连续的 `M` 合并注入，遇到点击/滚轮/文本会先落位，保证「先移动后点击」的顺序。
- 接管时光标钉在屏幕中心，拖到边缘也不丢位移；进程声明 DPI 感知，钩子坐标与光标坐标同系。
- 单次位移上限 ±2000px，防止异常事件把指针甩飞。
- 切回 Windows 时补发所有按下的键/按钮，断线同理，不会卡在拖拽状态。

## 线路协议

UTF-8、按行分隔，每行以 `\n` 结尾。

**控制连接（4321）**

| 行 | 含义 |
|---|---|
| `T <JSON字符串>` | 注入文本，引号/反斜杠/换行需正确转义 |
| `K <键名>` | 注入按键，如 `K Return`、`K ctrl+a`、`K F5`、`K KP_7` |
| `M <dx> <dy>` | 相对移动指针，单次上限 ±2000 |
| `B <按钮> down\|up` | 鼠标按钮，1 左 2 中 3 右 8 后退 9 前进 |
| `W up\|down\|left\|right` | 滚轮，对应 X11 按钮 4/5/6/7 |
| `C <JSON字符串>` | 写对端剪贴板（文本，双向） |
| `CUR <0\|1>` | Ubuntu 指针可见性：`0` 隐藏，`1` 显示；断开时自动恢复 |
| `PING` / `PONG` | 心跳，同时用来算往返延迟 |
| `BT` | Ubuntu 通知 Windows 切回本机（顶栏菜单「切回 Windows」） |
| `WHERE` → `DIR <目录>` | 查询 Ubuntu 的接收目录 |
| `OPEN` → `OPEN ok` | 让 Ubuntu 打开接收目录 |
| `QUIT` | 断开连接 |

**文件连接（4322 / 4323）**

| 行 | 含义 |
|---|---|
| `UPLOAD <总字节数> <文件名> [<续传偏移>]` | 传文件；偏移省略表示从头传（旧版客户端） |
| `RESUME <总字节数> <文件名>` → `HAVE <字节数>` | 问对端已有多少字节；老版本回 `ERR`，发送方据此退回从头传 |
| `CLIPIMG <图片字节数> <文本字节数>` | 传一张 PNG 写进对端剪贴板，随后是图、再是文本 |

要点：

- 键名与按钮编号走白名单，未知值拒绝并记日志；老客户端只发 `BackSpace`/`Return` 也兼容。
- 文件用 `.part` 临时文件接收，完成后原子替换；中断保留 `.part` 与 `.part.info`（记文件名与总大小）供续传，整份传完自动清理。
- 剪贴板图片不落盘、不进控制通道，几 MB 截图也不会撑爆按行解析。
- 明文 TCP，没有认证与加密——安全边界完全交给网络层，见「安全说明」。

## 从源码构建

### GitHub Actions

推 `main` 产出 Windows EXE 与 DEB 构建产物；推 `v*` 标签会自动创建 Release 并附上 `TypeBridge.exe`、`typebridge_<版本>_all.deb`。

### Windows

只要系统自带 .NET Framework 4.x，不需要 Visual Studio：

```text
双击 build.bat
```

它会自动找 csc、WPF 程序集和图标，产物是同目录的 `TypeBridge.exe`；想编译到别处就 `build.bat D:\out\TypeBridge.exe`（程序正在运行时也能编译，用这个方式避免覆盖失败）。

手动调用等价于：

```powershell
$wpf = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF"
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
  /out:TypeBridge.exe `
  /reference:System.dll /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
  "/reference:$wpf\PresentationFramework.dll" `
  "/reference:$wpf\PresentationCore.dll" `
  "/reference:$wpf\WindowsBase.dll" `
  /reference:"<System.Xaml.dll 的完整路径>" `
  TypeBridge.cs WpfSettings.cs
```

设置窗口是 WPF 写的，`System.Xaml.dll` 在 GAC 的 `Microsoft.NET\assembly\GAC_MSIL\System.Xaml` 下，路径各机不同，所以 `build.bat` 里用 `dir /s` 自动定位，手写命令时按实际路径填。`/target:winexe` 保证不弹控制台窗口。

### Ubuntu DEB

```bash
VERSION=3.5.19 bash ubuntu/build-deb.sh
# 产物：dist/typebridge_3.5.18_all.deb
```

包含 GTK 设置页、服务端、桌面菜单入口与 systemd 用户服务。

### PowerShell 旧版（仅键盘）

仓库里保留 `TypeBridge.ps1` 与 `启动输入桥.vbs`，给不能跑 EXE 的应急场景。它停留在早期形态：**只转发键盘、快捷键固定 `Ctrl+Alt+U`、不读鼠标与新的配置项**。要鼠标、可配置快捷键、图形安装包，请用 EXE + `.deb`。

## 目录结构

```text
TypeBridge/
├─ TypeBridge.cs                 Windows 客户端（托盘、钩子、协议）
├─ WpfSettings.cs                设置窗口（WPF）
├─ typebridge-ubuntu.py          Ubuntu 服务端
├─ typebridge-drop.py            Ubuntu 桌面「发送到Windows」出件箱
├─ TypeBridge.ps1                PowerShell 备用实现（仅键盘）
├─ assets/                       图标（SVG / PNG / ICO）
├─ ubuntu/
│  ├─ typebridge-ui.py           Ubuntu 图形设置页
│  ├─ typebridge.desktop         应用菜单入口
│  ├─ typebridge.service         systemd 用户服务
│  └─ build-deb.sh               DEB 打包脚本
├─ build.bat                     本地 EXE 构建
├─ typebridge.example.ini        配置模板
└─ .github/workflows/build.yml   CI 构建 / Release
```

`typebridge.ini`、`*.exe`、`*.deb`、`dist*/`、`__pycache__/` 等本机文件已在 `.gitignore` 里。

## 安全说明

协议是**明文 TCP，没有内置认证或 TLS**，安全边界完全由网络层决定：

- 推荐只在 Tailscale / WireGuard 这类加密私有网络里用。
- 或在可信局域网里用，并用防火墙限制 4321–4323。
- **不要**在公网路由上做端口转发。
- 能连上 4321 的人可以向 Ubuntu 当前焦点窗口注入按键、也能移动指针和点击；能连上 4323 的人可以给你投放文件（文件名已做穿越与非法字符过滤）。

Windows 端使用低级键盘/鼠标钩子：只在 Ubuntu 模式下拦截并转发，Windows 模式下全部放行，不记录、不落盘输入内容。部分安全软件会把全局钩子标为敏感行为，这是功能所必需的。

日志：Windows 在 EXE 同目录 `typebridge.log`（256KB 自动轮转），Ubuntu 用 `journalctl --user -u typebridge -f`。

## 常见问题

**开了 TUN / 虚拟网卡模式后很卡**

代理软件的 TUN 会接管默认路由，Tailscale 的 `100.64.0.0/10` 没有直连规则时会被丢进代理甚至绕到远端节点，往返从几毫秒涨到几百毫秒。两条路一起走：

- 让程序自己选路：`ip` 里同时写 Tailscale 和局域网地址，程序每次连接都会并行探测、挑最快的那条。日志里会写 `path pick: 192.168.1.20:4321 rtt=1.6ms 候选[...]`。
- 给代理加直连规则：`100.64.0.0/10`、`10.0.0.0/8`、`192.168.0.0/16`、`172.16.0.0/12` 设为 DIRECT。

**托盘显示未连接**

```bash
systemctl --user status typebridge --no-pager
ss -lntp | grep 432
tailscale status
```

再确认 Windows 里的 IP/端口、以及 Ubuntu 防火墙是否放行。

**连上了但按键没反应**

确认 Ubuntu 当前窗口有文本焦点，且是 X11 会话（Wayland 下注入不可靠）。

**拼音没有候选窗口**

确认 Ubuntu 输入法已启用，且 `~/.config/typebridge/config.json` 里 `auto_deactivate_fcitx` 为 `false`，改完 `systemctl --user restart typebridge`。

**快捷键没反应**

可能被别的软件占了（设置里会提示「已被其他程序占用」），也可能是安全软件拦了全局钩子。右键托盘可以直接切换，用来区分是快捷键问题还是转发问题。中文输入法开着时，某些组合键会被输入法先吃掉。

历史上出现过「切到 Ubuntu 后切不回 Windows」（3.4.0 的 bug，3.4.1 修复）：接管期间修饰键的 keydown 被钩子吞掉，`GetAsyncKeyState` 因此拿不到状态，组合键永远凑不齐。排查时看日志：正常一次往返会依次出现 `toggle keyboard -> Ubuntu`、`keyboard mode=Ubuntu`、`hook.enabled=True`，切回时是 `toggle hotkey hit (hook)`、`keyboard mode=Windows`。

**鼠标突然被收回 Windows**

如果提示「3 秒没收到 Ubuntu 回应」，说明心跳超时：先看日志有没有 `watchdog: no pong for …s`，再看 `network slow: rtt=…` 判断是不是链路问题（TUN、弱网、休眠唤醒）。

**只有托盘图标，没有主窗口**

这是设计如此：没有常驻输入窗口，设置窗口只在首次运行或手动打开时出现。

**PowerShell 版报编码错误**

`TypeBridge.ps1` 必须存成 UTF-8 with BOM，仓库里的文件已带 BOM。

## 已知限制

- 仅支持 X11；Wayland 原生会话未验证。
- 不传画面，不能当远程桌面用。
- 鼠标按相对位移转发，无法把指针「跳」到 Ubuntu 的某个绝对坐标。
- 不提供候选词界面，中文完全依赖 Ubuntu 输入法。
- 部分全屏游戏或使用原始输入的程序可能不兼容（独占全屏、Raw Input）。
- 键盘厂商自定义的额外按键可能没有标准 X11 键名。
- 边缘切换只有四条边，不支持斜角摆放。
- 文件传输是单条 TCP 流，链路本身约 6–8 MB/s（1 GB 约 2–3 分钟）；支持续传但不支持并行分段。
- 明文协议，必须配合 Tailscale 或可信局域网 + 防火墙。
