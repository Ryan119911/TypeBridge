# TypeBridge：Windows 键盘无窗口转发到 Ubuntu

TypeBridge 把 Windows 上的按键转发到 Ubuntu 当前焦点窗口。程序只常驻系统托盘，没有输入窗口；拼音输入和候选选字都由 **Ubuntu 输入法**完成。

## 1. 工作方式

```text
Windows 键盘
    │
    ├─ Windows 模式：所有按键正常进入 Windows
    │
    └─ Ubuntu 模式：字母、数字、标点、空格、退格、回车、
              方向键、PageUp/PageDown、Esc、Tab、Insert，
              以及 Ctrl/Alt/Win 组合键
              │  TCP / Tailscale，加密传输
              ▼
        Ubuntu typebridge.py
              │  xdotool
              ▼
        Ubuntu 当前焦点窗口 + Ubuntu 输入法
```

使用流程：

1. 开启 TypeBridge 后，Windows 一切照常，没有输入窗口。
2. 在任意 Windows 软件中按 **`Ctrl + Alt + U`**。
3. 直接在键盘上输入拼音；字母会发到 Ubuntu，Ubuntu 输入法显示候选。
4. 用数字键或空格选字，按退格删除，按回车确认。
5. 再按 **`Ctrl + Alt + U`**，立即恢复 Windows 输入。

程序不会在 Windows 中启动微信输入法组词，Windows 也不显示输入窗口。

## 2. 文件说明

| 文件 | 用途 |
|---|---|
| `TypeBridge.exe` | 编译后的原生 Windows 程序；运行后只有托盘图标 |
| `TypeBridge-v2-windowed.exe` | 旧版窗口式程序备份，不要使用 |
| `TypeBridge.cs` | EXE 的完整 C# 源码，无第三方依赖 |
| `build.bat` | 调用 Windows 自带 .NET Framework 编译器生成 EXE |
| `TypeBridge.ps1` | 可立即运行的 PowerShell 实现，架构与 EXE 相同 |
| `typebridge-ubuntu.py` | Ubuntu 端服务源码备份，已在 Ubuntu 部署 |
| `启动输入桥.vbs` | 无终端启动 PowerShell 版，推荐立即使用 |
| `启动输入桥.bat` | 有 EXE 时启动 EXE，否则调用 VBS |
| `typebridge.ini` | 可选配置，覆盖 Ubuntu IP 和端口 |
| `README.md` | 本说明 |

## 3. 立即使用

直接双击：

```text
启动输入桥.vbs
```

不会出现终端窗口。启动后只在通知区域出现 TypeBridge 托盘图标，并提示当前输入目标。

托盘操作：

- **双击图标**：切换 Windows / Ubuntu。
- **右键 → 切换到 Ubuntu / 切换到 Windows**：与快捷键相同。
- **右键 → 设置 Ubuntu 地址...**：填写**你自己的** Ubuntu IP 和端口，保存后立即重连。
- **右键 → 退出 TypeBridge**：完全退出并卸载键盘钩子。
- 图标和提示文字会显示当前目标及连接状态。
- 重复启动会自动退出第二个实例，避免重复安装键盘钩子或争用快捷键。
- 第一次运行且没有 `typebridge.ini` 时，程序会自动打开地址设置窗口。

每台 Windows 只连接它自己填写的 Ubuntu，不存在中央服务器，也不会连接到发布者的电脑。

`启动输入桥.bat` 也可使用，但 `cmd` 可能短暂闪现；要完全无终端请用 VBS 或 EXE。

## 4. 编译 EXE

无需安装 Visual Studio、Python 或任何第三方包。Windows 自带 .NET Framework 4.x 即可。

1. 双击 `build.bat`。
2. 成功后生成 `TypeBridge.exe`。
3. 双击 `TypeBridge.exe` 使用。

> 如果刚刚修改过 `TypeBridge.cs`，请重新运行 `build.bat`，确保 EXE 使用最新源码。

构建参数使用 `/target:winexe`，因此运行时不创建控制台窗口。

给他人分发时只需复制：

```text
TypeBridge.exe
typebridge.ini    （可选）
```

目标电脑不需要安装编译环境。

## 5. 全局快捷键与按键边界

### 已支持

| 按键 | Ubuntu 端行为 |
|---|---|
| 字母 `a-z` / `A-Z` | 发送字符，供 Ubuntu 输入法组拼音 |
| 数字 `0-9` | 发送字符，用于候选选择 |
| 空格 | 发送空格，用于上屏候选 |
| 常用标点 | 按当前键盘布局转为字符后发送 |
| 退格 | 发送 `K BackSpace` |
| 回车 | 发送 `K Return` |
| 方向键 `↑ ↓ ← →` | 发送 `K Up / Down / Left / Right` |
| `PageUp` / `PageDown` | 发送 `K Prior / Next` |
| `Esc` | 发送 `K Escape` |
| `Tab` | 发送 `K Tab` |
| `Insert` | 发送 `K Insert` |
| `Ctrl/Alt/Win + 字母/数字/功能键` | 发送组合键，例如 `K ctrl+a`、`K alt+b`、`K super+e` |
| `Ctrl + Alt + U` | 仅切换输入目标，不发送 |

Windows 输入法不会参与组词，候选完全由 Ubuntu 输入法负责。

### 组合键转发规则

- Windows 模式下，一切照旧，组合键仍交给 Windows。
- Ubuntu 模式下，`Ctrl/Alt/Win + 字母/数字/功能键` 会被转发到 Ubuntu，同时阻止其进入 Windows 当前软件。
- 修饰键本身不会被拦截，因此单独按下 Ctrl、Alt 或 Win 不会误触发 Windows 快捷键。
- `Ctrl + Alt + U` 永远用于切换输入目标，不会被转发到 Ubuntu。

## 6. 输入模式说明

### Windows 模式

- 全局钩子仍然安装，但 `Enabled=false`，所有按键原样放行。
- 不读取、不缓存、不记录输入内容。
- 托盘提示“TypeBridge → Windows”。

### Ubuntu 模式

- 只拦截字符、退格、回车、方向键、PageUp/PageDown、Esc、Tab 和组合键。
- 拦截的按键不会同时进入 Windows 当前软件。
- `Ctrl + Alt + U` 仍然用于切换输入目标，不会被转发。
- 连接断开时，被拦截的字符无法发送；请先看托盘提示是否“未连接”。
- 托盘提示“TypeBridge → Ubuntu”。

## 7. 配置 Ubuntu 地址

默认只监听本机占位地址 `127.0.0.1`。第一次运行或通过托盘菜单“设置 Ubuntu 地址...”填写你自己的 Ubuntu 地址：

```ini
ip=100.x.y.z        # 你的 Tailscale IP，或局域网 IP / 主机名
port=4321
```

局域网直连示例：

```ini
ip=192.168.1.20
port=4321
```

也可以直接编辑程序同目录的 `typebridge.ini`，修改后重启或重新保存设置。

## 8. Ubuntu 端要求

Ubuntu 继续运行原有 `typebridge.py` 用户服务，监听 `0.0.0.0:4321`。Windows 端协议保持向下兼容：

| 行协议（UTF-8、`\n` 结尾） | 含义 |
|---|---|
| `T <JSON字符串>` | 注入文本；引号、反斜杠、换行必须正确 JSON 转义 |
| `K BackSpace` | 退格 |
| `K Return` | 回车 |
| `K Up / Down / Left / Right` | 方向键 |
| `K Prior / Next` | PageUp / PageDown |
| `K Escape / Tab / Insert` | Esc、Tab、Insert |
| `K ctrl+a` / `K alt+b` / `K super+e` | 组合键，修饰键支持 `ctrl / alt / shift / super` |
| `PING` | 连通性测试，返回 `PONG` |
| `QUIT` | 断开连接 |

Ubuntu 端已支持上述键名和组合键；旧客户端只发送 `BackSpace` 与 `Return`，仍然兼容。

## 9. 排查

### 托盘图标存在，但快捷键无效

可能 `Ctrl+Alt+U` 被其他软件占用。启动提示会说明；此时右键托盘图标切换。

### 切到 Ubuntu 后 Windows 仍在输入

检查托盘提示是否为“TypeBridge → Ubuntu”。若仍为 Windows，说明快捷键未触发，使用托盘菜单切换。

### 按键没有出现在 Ubuntu

1. 确认 Ubuntu 当前窗口有文本焦点。
2. 确认托盘显示“已连接”。
3. 用下面命令测试 PING/PONG：

```powershell
$server = "100.x.y.z"   # 改成你的 Ubuntu 地址
$c = New-Object System.Net.Sockets.TcpClient
$c.ConnectAsync($server, 4321).Wait(1000)
$s = $c.GetStream()
$b = [Text.Encoding]::UTF8.GetBytes("PING`n")
$s.Write($b, 0, $b.Length)
$s.Flush()
$buf = New-Object byte[] 16
$s.ReadTimeout = 1000
$n = $s.Read($buf, 0, 16)
[Text.Encoding]::UTF8.GetString($buf, 0, $n)
$c.Close()
```

预期 `PONG`。

### 中文候选没有出现

确认 Ubuntu 输入法已启用，并处于中文输入状态。TypeBridge 只负责把拼音按键送到 Ubuntu，候选窗口由 Ubuntu 输入法显示。

### PowerShell 版启动时报编码错误

`TypeBridge.ps1` 必须保存为 **UTF-8 with BOM**。当前文件已带 BOM。

### 安全软件提示键盘钩子

无窗口转发必须使用 Windows 低级键盘钩子。程序只在 Ubuntu 模式拦截字符；Windows 模式全部放行，也不记录按键。若公司安全策略禁止此类钩子，应改用有输入窗口的版本。

## 10. 验收清单

1. 双击 VBS 后只有托盘图标，没有窗口和常驻终端。
2. Windows 模式下在各软件中打字正常。
3. 按 `Ctrl+Alt+U` 后，Windows 本地软件不再收到字母，Ubuntu 收到拼音。
4. Ubuntu 输入法能显示候选，数字键或空格能选字。
5. 退格能删除 Ubuntu 中文字符，回车能确认。
6. 方向键、PageUp/PageDown、Esc、Tab 能在 Ubuntu 正常生效。
7. `Ctrl+A`、`Alt+B`、`Win+E` 等组合键能转发到 Ubuntu，且不再触发 Windows 本地快捷键。
8. 再按快捷键后，Windows 输入立即恢复。
9. 托盘退出后，快捷键与键盘钩子完全失效。
