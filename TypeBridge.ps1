# ============================================================
#  TypeBridge.ps1 - 无窗口全局输入重定向（PowerShell 版）
#  Ctrl+Alt+U：Windows <-> Ubuntu；中文由 Ubuntu 输入法完成。
# ============================================================
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 只允许一个实例，避免重复安装全局键盘钩子。
$mutex = New-Object System.Threading.Mutex($false, "Local\TypeBridge-SingleInstance-7C4A")
if (-not $mutex.WaitOne(0)) { exit }

Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @"
using System;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public class HotkeyForm : Form {
    public bool HotKeyAvailable = false;
    public event EventHandler HotKeyPressed;
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x5472;
    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        HotKeyAvailable = RegisterHotKey(Handle, HOTKEY_ID, 0x0001 | 0x0002, (uint)Keys.U);
    }
    protected override void OnHandleDestroyed(EventArgs e) {
        if (HotKeyAvailable) { UnregisterHotKey(Handle, HOTKEY_ID); HotKeyAvailable = false; }
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID) {
            EventHandler handler = HotKeyPressed;
            if (handler != null) { handler(this, EventArgs.Empty); }
            return;
        }
        base.WndProc(ref m);
    }
}

public sealed class BridgeKeyEventArgs : EventArgs {
    public string Text { get; private set; }
    public string KeyName { get; private set; }
    public BridgeKeyEventArgs(string text, string keyName) { Text = text; KeyName = keyName; }
}

public sealed class GlobalKeyboardHook : IDisposable {
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdData {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private readonly HookProc callback;
    private IntPtr hook = IntPtr.Zero;
    private readonly HashSet<int> suppressedKeys = new HashSet<int>();

    [DllImport("user32.dll", SetLastError=true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)]
    private static extern IntPtr GetModuleHandle(string moduleName);
    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState,
        [Out] StringBuilder text, int textSize, uint flags, IntPtr layout);

    public bool Enabled { get; set; }
    public event EventHandler<BridgeKeyEventArgs> Key;

    public GlobalKeyboardHook() { callback = HookCallback; }

    public void Install() {
        if (hook != IntPtr.Zero) { return; }
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static List<string> ActiveModifiers() {
        List<string> modifiers = new List<string>();
        if (((GetAsyncKeyState(0xA2) & 0x8000) != 0) ||
            ((GetAsyncKeyState(0xA3) & 0x8000) != 0)) { modifiers.Add("ctrl"); }
        if (((GetAsyncKeyState(0xA4) & 0x8000) != 0) ||
            ((GetAsyncKeyState(0xA5) & 0x8000) != 0)) { modifiers.Add("alt"); }
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) { modifiers.Add("shift"); }
        if (((GetAsyncKeyState(0x5B) & 0x8000) != 0) ||
            ((GetAsyncKeyState(0x5C) & 0x8000) != 0)) { modifiers.Add("super"); }
        return modifiers;
    }

    private static bool IsToggleHotKey(int virtualKey, List<string> modifiers) {
        return virtualKey == (int)Keys.U &&
            modifiers.Count == 2 &&
            modifiers.Contains("ctrl") &&
            modifiers.Contains("alt");
    }

    private static string SpecialKeyName(int virtualKey) {
        switch ((Keys)virtualKey) {
            case Keys.Back: return "BackSpace";
            case Keys.Return: return "Return";
            case Keys.Tab: return "Tab";
            case Keys.Escape: return "Escape";
            case Keys.Space: return "space";
            case Keys.Prior: return "Prior";
            case Keys.Next: return "Next";
            case Keys.Home: return "Home";
            case Keys.End: return "End";
            case Keys.Delete: return "Delete";
            case Keys.Insert: return "Insert";
            case Keys.Up: return "Up";
            case Keys.Down: return "Down";
            case Keys.Left: return "Left";
            case Keys.Right: return "Right";
            default: return null;
        }
    }

    private static string BaseKeyName(int virtualKey) {
        string special = SpecialKeyName(virtualKey);
        if (special != null) { return special; }
        if (virtualKey >= 0x41 && virtualKey <= 0x5A) {
            return ((char)('a' + virtualKey - 0x41)).ToString();
        }
        if (virtualKey >= 0x30 && virtualKey <= 0x39) {
            return ((char)('0' + virtualKey - 0x30)).ToString();
        }
        if (virtualKey >= 0x70 && virtualKey <= 0x87) {
            return "F" + (virtualKey - 0x6F).ToString();
        }
        switch ((Keys)virtualKey) {
            case Keys.OemSemicolon: return "semicolon";
            case Keys.OemQuestion: return "slash";
            case Keys.Oemtilde: return "grave";
            case Keys.OemOpenBrackets: return "bracketleft";
            case Keys.OemCloseBrackets: return "bracketright";
            case Keys.OemMinus: return "minus";
            case Keys.Oemplus: return "equal";
            case Keys.Oemcomma: return "comma";
            case Keys.OemPeriod: return "period";
            case Keys.OemPipe: return "backslash";
            case Keys.OemQuotes: return "apostrophe";
            default: return null;
        }
    }

    private static string KeyText(KbdData data) {
        byte[] state = new byte[256];
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) { state[0x10] = 0x80; }
        if ((GetKeyState(0x14) & 1) != 0) { state[0x14] = 0x01; }
        if ((GetKeyState(0x90) & 1) != 0) { state[0x90] = 0x01; }
        uint thread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        IntPtr layout = GetKeyboardLayout(thread);
        StringBuilder text = new StringBuilder(8);
        int result = ToUnicodeEx(data.VkCode, data.ScanCode, state, text, text.Capacity, 0, layout);
        if (result <= 0 || text.Length == 0) { return null; }
        foreach (char c in text.ToString()) {
            if (char.IsControl(c)) { return null; }
        }
        return text.ToString();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        if (nCode >= 0) {
            int message = wParam.ToInt32();
            bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            bool up = message == WM_KEYUP || message == WM_SYSKEYUP;
            if (down || up) {
                KbdData data = (KbdData)Marshal.PtrToStructure(lParam, typeof(KbdData));
                int vk = (int)data.VkCode;
                if (up) {
                    if (suppressedKeys.Remove(vk)) { return (IntPtr)1; }
                    return CallNextHookEx(hook, nCode, wParam, lParam);
                }
                if (Enabled) {
                    List<string> modifiers = ActiveModifiers();
                    if (!IsToggleHotKey(vk, modifiers)) {
                        string baseKey = BaseKeyName(vk);
                        string text = null;
                        string keyName = null;
                        if (modifiers.Count > 0 && baseKey != null) {
                            keyName = string.Join("+", modifiers.ToArray()) + "+" + baseKey;
                        } else if (modifiers.Count == 0) {
                            keyName = SpecialKeyName(vk);
                            if (keyName == null) { text = KeyText(data); }
                        }
                        if (text != null || keyName != null) {
                            suppressedKeys.Add(vk);
                            EventHandler<BridgeKeyEventArgs> handler = Key;
                            if (handler != null) {
                                try { handler(this, new BridgeKeyEventArgs(text, keyName)); }
                                catch { }
                            }
                            return (IntPtr)1;
                        }
                    }
                }
            }
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    public void Dispose() {
        Enabled = false;
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        GC.SuppressFinalize(this);
    }
}
"@

# ---- 配置 ----
$ini = Join-Path $PSScriptRoot "typebridge.ini"
$ip = "127.0.0.1"; $port = 4321
if (Test-Path $ini) {
    Get-Content $ini | ForEach-Object {
        $kv = $_ -split '=', 2
        if ($kv.Count -eq 2) {
            switch ($kv[0].Trim().ToLower()) {
                "ip"   { $ip = $kv[1].Trim() }
                "port" { $port = [int]$kv[1].Trim() }
            }
        }
    }
}
$script:configLoaded = Test-Path $ini

$script:client = $null
$script:stream = $null
$script:ubuntuMode = $false

# ---- 隐藏窗体只用于接收全局热键；不显示输入窗口 ----
$form = New-Object HotkeyForm
$form.ShowInTaskbar = $false
$form.Visible = $false
[void]$form.Handle

$hook = New-Object GlobalKeyboardHook
$hook.Install()
$hook.Enabled = $false

# ---- 托盘 ----
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$toggleItem = New-Object System.Windows.Forms.ToolStripMenuItem
$toggleItem.Text = "切换到 Ubuntu`tCtrl+Alt+U"
$settingsItem = New-Object System.Windows.Forms.ToolStripMenuItem
$settingsItem.Text = "设置 Ubuntu 地址..."
$exitItem = New-Object System.Windows.Forms.ToolStripMenuItem
$exitItem.Text = "退出 TypeBridge"
[void]$menu.Items.Add($toggleItem)
[void]$menu.Items.Add($settingsItem)
[void]$menu.Items.Add($exitItem)

$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon = [System.Drawing.SystemIcons]::Application
$tray.ContextMenuStrip = $menu
$tray.Visible = $true

function Update-Status {
    if ($script:ubuntuMode) {
        $conn = if ($script:stream) { "已连接" } else { "未连接" }
        $tray.Text = "TypeBridge → Ubuntu ($conn)"
        $tray.Icon = [System.Drawing.SystemIcons]::Information
        $toggleItem.Text = "切换到 Windows`tCtrl+Alt+U"
    } else {
        $tray.Text = "TypeBridge → Windows"
        $tray.Icon = [System.Drawing.SystemIcons]::Application
        $toggleItem.Text = "切换到 Ubuntu`tCtrl+Alt+U"
    }
}

function Show-TrayTip([string]$title, [string]$text) {
    $tray.BalloonTipTitle = $title
    $tray.BalloonTipText = $text
    $tray.BalloonTipIcon = [System.Windows.Forms.ToolTipIcon]::Info
    $tray.ShowBalloonTip(1200)
}

function Set-InputMode([bool]$remote, [bool]$notify = $true) {
    $script:ubuntuMode = $remote
    $hook.Enabled = $remote
    Update-Status
    if ($notify) {
        $target = if ($remote) { "Ubuntu（使用 Ubuntu 输入法）" } else { "Windows" }
        Show-TrayTip "TypeBridge" "当前输入：$target"
    }
}

function Toggle-Mode { Set-InputMode (-not $script:ubuntuMode) }

function Close-Connection {
    try { if ($script:client) { $script:client.Close() } } catch {}
    $script:client = $null
    $script:stream = $null
}

function Send-Line([string]$line) {
    if (-not $script:stream) { return $false }
    try {
        $b = [System.Text.Encoding]::UTF8.GetBytes($line + "`n")
        $script:stream.Write($b, 0, $b.Length)
        $script:stream.Flush()
        return $true
    } catch {
        Close-Connection
        Update-Status
        return $false
    }
}

function Send-Text([string]$text) {
    if ($text.Length -eq 0) { return $true }
    $json = ConvertTo-Json -InputObject $text -Compress
    return (Send-Line ("T " + $json))
}

function Save-Config {
    try {
        Set-Content -Path $ini -Value @("ip=$script:ip", "port=$script:port") -Encoding ASCII
        return $true
    } catch {
        [void][System.Windows.Forms.MessageBox]::Show(
            "无法保存配置文件：`r`n$ini`r`n`r`n$($_.Exception.Message)",
            "TypeBridge",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error)
        return $false
    }
}

function Show-SettingsDialog([bool]$firstRun) {
    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = if ($firstRun) { "TypeBridge 首次设置" } else { "TypeBridge 设置" }
    $dialog.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dialog.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false
    $dialog.ShowInTaskbar = $false
    $dialog.TopMost = $true
    $dialog.ClientSize = New-Object System.Drawing.Size(420, 220)

    $hostLbl = New-Object System.Windows.Forms.Label
    $hostLbl.Text = "Ubuntu 地址（Tailscale IP、局域网 IP 或主机名）"
    $hostLbl.AutoSize = $true
    $hostLbl.Location = New-Object System.Drawing.Point(16, 16)

    $ipBox = New-Object System.Windows.Forms.TextBox
    $ipBox.Text = $script:ip
    $ipBox.SetBounds(16, 42, 388, 26)

    $portLbl = New-Object System.Windows.Forms.Label
    $portLbl.Text = "端口"
    $portLbl.AutoSize = $true
    $portLbl.Location = New-Object System.Drawing.Point(16, 80)

    $portBox = New-Object System.Windows.Forms.TextBox
    $portBox.Text = [string]$script:port
    $portBox.SetBounds(16, 104, 100, 26)

    $hint = New-Object System.Windows.Forms.Label
    $hint.Text = "默认 4321；请填写你自己的 Ubuntu 地址。"
    $hint.AutoSize = $true
    $hint.ForeColor = [System.Drawing.Color]::Gray
    $hint.Location = New-Object System.Drawing.Point(130, 108)

    $err = New-Object System.Windows.Forms.Label
    $err.AutoSize = $false
    $err.SetBounds(16, 138, 388, 34)
    $err.ForeColor = [System.Drawing.Color]::Firebrick

    $cancelBtn = New-Object System.Windows.Forms.Button
    $cancelBtn.Text = "取消"
    $cancelBtn.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $cancelBtn.SetBounds(230, 180, 80, 30)

    $saveBtn = New-Object System.Windows.Forms.Button
    $saveBtn.Text = "保存"
    $saveBtn.SetBounds(320, 180, 84, 30)
    $saveBtn.add_Click({
        $newHost = $ipBox.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($newHost)) {
            $err.Text = "请输入 Ubuntu 地址。"
            return
        }
        $newPort = 0
        if (-not [int]::TryParse($portBox.Text.Trim(), [ref]$newPort) -or
            $newPort -lt 1 -or $newPort -gt 65535) {
            $err.Text = "端口必须是 1 到 65535 之间的数字。"
            return
        }
        $script:ip = $newHost
        $script:port = $newPort
        if (-not (Save-Config)) { return }
        $script:configLoaded = $true
        $dialog.DialogResult = [System.Windows.Forms.DialogResult]::OK
        $dialog.Close()
    })

    $dialog.AcceptButton = $saveBtn
    $dialog.CancelButton = $cancelBtn
    $dialog.Controls.Add($hostLbl)
    $dialog.Controls.Add($ipBox)
    $dialog.Controls.Add($portLbl)
    $dialog.Controls.Add($portBox)
    $dialog.Controls.Add($hint)
    $dialog.Controls.Add($err)
    $dialog.Controls.Add($cancelBtn)
    $dialog.Controls.Add($saveBtn)

    [void]$dialog.ShowDialog()
    $saved = $dialog.DialogResult -eq [System.Windows.Forms.DialogResult]::OK
    $dialog.Dispose()

    if ($saved) {
        Close-Connection
        Update-Status
        Show-TrayTip "设置已保存" "正在连接 $script:ip`:$script:port"
    }
}

$hook.add_Key({
    param($sender, $e)
    if (-not $script:ubuntuMode) { return }
    if ($null -ne $e.Text) { [void](Send-Text $e.Text) }
    elseif ($e.KeyName) { [void](Send-Line ("K " + $e.KeyName)) }
})
$form.add_HotKeyPressed({ Toggle-Mode })
$toggleItem.add_Click({ Toggle-Mode })
$settingsItem.add_Click({ Show-SettingsDialog $false })
$tray.add_DoubleClick({ Toggle-Mode })
$exitItem.add_Click({ [System.Windows.Forms.Application]::Exit() })

$timerConn = New-Object System.Windows.Forms.Timer
$timerConn.Interval = 1000
$timerConn.add_Tick({
    if ($script:stream) { return }
    $c = $null
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        if ($c.ConnectAsync($script:ip, $script:port).Wait(400)) {
            $script:client = $c
            $script:stream = $c.GetStream()
        } else { $c.Close() }
    } catch {
        try { if ($c) { $c.Close() } } catch {}
    }
    Update-Status
})
$timerConn.Start()

try {
    Set-InputMode $false $false
    Show-TrayTip "TypeBridge 已启动" "Ctrl + Alt + U 切换到 Ubuntu 输入"
    if (-not $form.HotKeyAvailable) {
        Show-TrayTip "快捷键不可用" "Ctrl+Alt+U 已被占用，请用托盘菜单切换"
    }
    if (-not $script:configLoaded) {
        Show-SettingsDialog $true
    }
    [System.Windows.Forms.Application]::Run()
} finally {
    $timerConn.Stop()
    $hook.Dispose()
    $tray.Visible = $false
    $tray.Dispose()
    $form.Dispose()
    Close-Connection
    try { $mutex.ReleaseMutex() } catch {}
    $mutex.Dispose()
}
