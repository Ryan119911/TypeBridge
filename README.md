# TypeBridge

**在 Windows 上直接打字，把键盘输入实时发送到 Ubuntu 当前焦点窗口。**

TypeBridge 是一个无窗口、托盘常驻的 Windows 输入桥。它只转发键盘输入，不传画面、不控制鼠标。中文不在 Windows 端提交，而是把拼音和候选选择键送到 Ubuntu，由 **Ubuntu 输入法**完成组词和选字。

[![Build Windows EXE](https://github.com/Ryan119911/TypeBridge/actions/workflows/build.yml/badge.svg)](https://github.com/Ryan119911/TypeBridge/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/Ryan119911/TypeBridge)](https://github.com/Ryan119911/TypeBridge/releases)

## 下载

- 最新 EXE：<https://github.com/Ryan119911/TypeBridge/releases/latest>
- 项目源码：<https://github.com/Ryan119911/TypeBridge>

每台 Windows 都连接**它自己填写的 Ubuntu**。项目没有中央服务器，也不会连接到发布者的电脑。

## 特性

- **无输入窗口**：启动后只常驻系统托盘，不打断正常 Windows 输入。
- **全局切换**：在任何软件中按 `Ctrl + Alt + U`，在 Windows 输入和 Ubuntu 输入之间切换。
- **Ubuntu 输入法选字**：拼音字母、数字、空格发往 Ubuntu，候选窗口由 Ubuntu 输入法显示。
- **发送后不堆字**：Ubuntu 模式下的按键不会同时进入 Windows 当前软件。
- **特殊按键**：方向键、PageUp/PageDown、Esc、Tab、Insert。
- **组合键**：Ctrl/Alt/Win + 字母、数字、功能键，例如 `Ctrl+A`、`Alt+B`、`Win+E`。
- **独立配置**：首次运行填入自己的 Ubuntu IP 和端口，保存在本地 `typebridge.ini`。
- **低依赖**：Windows 端使用系统自带 .NET Framework；Ubuntu 端只需要 Python 3、GTK、xdotool 和可用的 X11 输入法。
- **单实例**：重复启动会自动退出第二个实例，避免重复键盘钩子。

## 架构

```text
┌──────────────────────────────┐
│ Windows 10/11                │
│                              │
│  TypeBridge.exe              │
│    ├─ 托盘图标 / 设置窗口     │
│    ├─ Ctrl+Alt+U 模式切换    │
│    └─ 低级键盘钩子            │
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

### 1. 准备 Ubuntu

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

例如输出 `100.x.y.z`。也可以使用局域网 IP 或主机名。

### 2. 配置 Windows

1. 从 [Releases](https://github.com/Ryan119911/TypeBridge/releases/latest) 下载 `TypeBridge.exe`。
2. 双击运行，不需要安装。
3. 第一次启动会自动打开设置窗口；填写**你自己的 Ubuntu 地址和端口**并保存。
4. 也可以右键托盘图标，选择“设置 Ubuntu 地址...”随时修改。

Windows 端配置文件为本机 `typebridge.ini`：

```ini
ip=100.x.y.z
port=4321
```

该文件不会提交到 Git 仓库。

### 3. 开始输入

1. 在 Ubuntu 中打开任意可编辑窗口并保持焦点。
2. 在 Windows 中按 `Ctrl + Alt + U`。
3. 直接输入拼音，使用数字键或空格选字。
4. 按 `Ctrl + Alt + U` 切回 Windows。

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
| Ctrl/Alt/Win + 字母、数字、功能键 | `K ctrl+a`、`K alt+b`、`K super+e` 等组合 |
| `Ctrl + Alt + U` | 仅切换模式，不发送到 Ubuntu |

在 Ubuntu 模式下，被转发的按键不会再进入 Windows 当前软件；单独的 Ctrl、Alt、Win 修饰键不会被拦截。

## 安全说明

TypeBridge 的线路协议是**明文 TCP，没有内置认证或 TLS**。安全边界依赖网络层：

- 推荐只在 Tailscale / WireGuard 等加密私有网络中使用。
- 或在可信局域网中使用，并通过防火墙限制 4321 端口。
- **不要**在公网路由器上直接端口转发 4321。
- 任何能连接 4321 端口的人都能向 Ubuntu 当前焦点窗口注入按键。

Windows 端使用低级键盘钩子：

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
| `K ctrl+a` / `K alt+b` / `K super+e` | 组合键，支持 `ctrl / alt / shift / super` |
| `PING` | 连通性测试，返回 `PONG` |
| `QUIT` | 断开连接 |

`K` 键名使用白名单校验，未知键名会被拒绝。旧客户端只发送 `BackSpace` 和 `Return` 时仍然兼容。

非 ASCII 的 `T` 文本会由 Ubuntu 端写入剪贴板后粘贴，可能覆盖当前剪贴板内容。

## 从源码构建

### GitHub Actions

仓库自带 `.github/workflows/build.yml`。每次推送到 `main` 会生成构建产物；推送 `v*` 标签会自动创建 Release 并附带 `TypeBridge.exe`。

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

### PowerShell 备用版

仓库同时保留 `TypeBridge.ps1` 和 `启动输入桥.vbs`，适合不编译 EXE 时直接运行。双击 VBS 可以无终端启动 PowerShell 版；正式分发仍建议使用 EXE。

## 目录结构

```text
TypeBridge/
├─ TypeBridge.cs                 Windows 客户端源码
├─ TypeBridge.ps1                PowerShell 备用实现
├─ typebridge-ubuntu.py          Ubuntu 服务端
├─ build.bat                     本地 EXE 构建脚本
├─ typebridge.example.ini        Windows 配置模板
├─ 启动输入桥.vbs                 无终端启动 PowerShell 版
├─ 启动输入桥.bat                 备用启动器
├─ .github/workflows/build.yml   GitHub Actions 构建/Release
└─ README.md
```

`typebridge.ini`、`*.exe`、`__pycache__/` 等本机文件已加入 `.gitignore`。

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

### Ctrl+Alt+U 无反应

快捷键可能被其他软件占用。右键托盘图标选择模式切换；如果仍然无效，检查安全软件是否拦截了全局键盘钩子。

### 启动 EXE 后只有托盘，没有主窗口

这是预期行为。TypeBridge 没有常驻输入窗口；设置窗口只在首次运行或手动打开设置时出现。

### PowerShell 版报编码错误

`TypeBridge.ps1` 必须保存为 UTF-8 with BOM。仓库当前文件已带 BOM。

## 已知限制

- 仅支持 X11；Wayland 原生会话未验证。
- 不传输画面，不控制鼠标，不支持远程桌面场景。
- 依赖 Ubuntu 输入法本身；TypeBridge 不提供候选词界面。
- 目标程序必须能接收 X11 键盘事件；部分全屏游戏或使用原始输入的程序可能不兼容。
- 明文 TCP 协议，必须配合 Tailscale、可信局域网和防火墙使用。
- `Ctrl + Alt + U` 为保留快捷键，不转发到 Ubuntu。
