using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TypeBridgeApp
{
    internal sealed partial class BridgeApplicationContext : ApplicationContext
    {
        private readonly HotkeyForm hotkeyForm = new HotkeyForm();
        private readonly GlobalKeyboardHook hook = new GlobalKeyboardHook();
        private readonly GlobalMouseHook mouseHook = new GlobalMouseHook();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ToastForm toast = new ToastForm();
        private readonly Icon appIcon;
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem toggleItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem mouseToggleItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem settingsItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem administratorItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem sendFileItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem openRemoteDirItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem dropPadItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem exitItem = new ToolStripMenuItem();
        private readonly Timer connectionTimer = new Timer();
        private DateTime lastHookHealUtc = DateTime.UtcNow;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        private TcpClient client;
        private NetworkStream stream;
        private string ip = "127.0.0.1";
        /// <summary>ip 可以写成"地址1, 地址2"，连接时并行探测，自动用延迟最低的那个。</summary>
        private string[] hosts = new string[0];
        private long pingSentTs;
        private int lastSlowWarnTick;
        private double lastRttMs;
        private int port = 4321;
        private int filePort = 4322;
        private int fileRecvPort = 4323;
        private string recvDir = string.Empty;      // 从 Ubuntu 收到的文件放哪
        private string remoteDir = string.Empty;    // 发到 Ubuntu 时落到哪个目录
        private string remoteResolvedDir = string.Empty; // Ubuntu 端实际解析出来的接收目录
        private TcpListener fileListener;
        private volatile bool receiverRunning;
        private DropPadForm dropPad;
        private FileZoneForm fileZone;
        private Timer fileZoneTimer;
        private int mouseHz = 250;
        private Hotkey keyboardHotkey = Hotkey.DefaultKeyboard();
        private Hotkey mouseHotkey = Hotkey.DefaultMouse();
        private bool linkMouse = true;
        private bool edgeSwitch = true;
        private string switchEdge = "right";
        // 摆放位置（方块相对本机的偏移，"dx,dy"），四个斜角和自由位置都靠它记住
        private string layoutOffset = string.Empty;
        // 切换成功时右下角浮层：组合键方案需要提示"按什么切回"，边缘/整键切换后反而碍事，默认关。
        // 想恢复在 typebridge.ini 里设 switch_toast=1。
        private bool switchToast;
        private int remoteWidth = 1920;
        private int remoteHeight = 1080;
        private bool keyboardMode;
        private bool mouseMode;
        // 反向共享：Ubuntu 用它的键鼠操作本机。读取线程直接注入（延迟最低），
        // 托盘文案等界面动作再切回 UI 线程。
        private volatile bool remoteDrive;
        private bool reverseEnable = true;
        private DateTime remoteDriveSince;
        // 上一次"本机输入收回控制权"的时刻：同一次按键别再触发切换热键
        private DateTime localTakeoverAt;
        private DateTime lastPongUtc = DateTime.UtcNow;

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
        private uint lastClipboardSeq;
        private bool configLoaded;
        private bool settingsOpen;
        private bool disposed;
        // 同名文件并发上传要串行，避免两条连接共写一个 .part
        private readonly Dictionary<string, object> uploadGates = new Dictionary<string, object>();

        // 网络收发全部在后台线程执行。全局钩子也挂在 UI 线程上，如果在钩子回调里
        // 同步写网络，Tailscale 链路一旦抖动（中继切换/半死连接），Write 会阻塞
        // 几秒到几十秒：钩子回调超时，系统把按键鼠标放行给 Windows —— 表现就是
        // "两台电脑同时响应"、修饰键状态失步、切换热键失灵。改队列后 UI 线程只入队。
        // 每次连接用独立的队列和世代号：重连时旧发送线程即使还活着，
        // 也只碰自己的旧队列 / 旧流，不会抢走新消息或误关新连接。
        private ControlQueue outgoing;
        private volatile int senderGeneration;
        private volatile bool connecting;
        // 控制连接实测最快的地址，文件通道复用它
        private string activeHost;
        // 设置窗口保存是先断开再异步重连，恢复键鼠的意图挂到连接成功回调里执行
        private bool resumeKeyboardAfterReconnect;
        private bool resumeMouseAfterReconnect;
        private DateTime lastEnqueueUtc;
        private DateTime lastToggleUtc;
        private DateTime lastMouseToggleUtc;
        private readonly Timer clipboardTimer = new Timer();
        private string lastLocalClip;
        private string lastRemoteClip;
        // 图片走文件通道（CLIPIMG）传输，用 PNG 的 MD5 做去重指纹
        private string lastLocalImageKey;
        private bool suppressImageEcho;
        private int imageSending;
        private System.Threading.Thread readerThread;
        private volatile bool readerRunning;
        private int queuedIncoming;
        private int queuedIncomingCharacters;

        internal BridgeApplicationContext()
        {
            LoadConfiguration();
            appIcon = LoadAppIcon();
            Log.Write("---- TypeBridge " + Application.ProductVersion + " start, ip=" + ip
                + ":" + port + " keyboard=" + keyboardHotkey + " mouse=" + mouseHotkey
                + " linkMouse=" + linkMouse);

            hotkeyForm.ShowInTaskbar = false;
            hotkeyForm.Visible = false;
            IntPtr unusedHandle = hotkeyForm.Handle;
            GC.KeepAlive(unusedHandle);
            hotkeyForm.KeyboardHotKeyPressed += delegate
            {
                Log.Write("toggle hotkey hit (RegisterHotKey)");
                ToggleKeyboardMode();
            };
            hotkeyForm.MouseHotKeyPressed += delegate
            {
                Log.Write("mouse hotkey hit (RegisterHotKey)");
                ToggleMouseMode();
            };

            hook.Key += HookKey;
            hook.ToggleRequested += delegate { ToggleKeyboardMode(); };
            hook.MouseToggleRequested += delegate { ToggleMouseMode(); };
            hook.Install();
            hook.Enabled = false;

            mouseHook.Moved += HookMouseMove;
            mouseHook.Button += HookMouseButton;
            mouseHook.Wheel += HookMouseWheel;
            mouseHook.EdgeEntered += delegate { OnEdgeEntered(); };
            mouseHook.EdgeReturned += delegate { OnEdgeReturned(); };
            mouseHook.LocalInput += delegate { OnLocalInputDuringRemoteDrive(); };
            hook.LocalInput += delegate { OnLocalInputDuringRemoteDrive(); };
            mouseHook.DragEdgeReached += delegate { ShowFileZone(); };
            mouseHook.SwitchEdge = switchEdge;
            mouseHook.RemoteWidth = remoteWidth;
            mouseHook.RemoteHeight = remoteHeight;
            mouseHook.Install();
            mouseHook.SetRate(mouseHz);
            mouseHook.Enabled = false;

            toggleItem.Click += delegate { ToggleKeyboardMode(); };
            mouseToggleItem.Click += delegate { ToggleMouseMode(); };
            settingsItem.Text = "设置...";
            settingsItem.Click += delegate { ShowSettings(false); };
            administratorItem.Text = AdministratorMode.IsEnabled
                ? "管理员模式已开启" : "以管理员权限重启（兼容管理员终端）";
            administratorItem.Enabled = !AdministratorMode.IsEnabled;
            administratorItem.Click += delegate { RestartAsAdministrator(); };
            sendFileItem.Text = "发送文件到 Ubuntu...";
            sendFileItem.Click += delegate { SendFilesDialog(); };
            openRemoteDirItem.Text = "在 Ubuntu 上打开接收目录";
            openRemoteDirItem.Click += delegate
            {
                if (stream == null)
                {
                    ShowTrayTip("TypeBridge", "尚未连接 Ubuntu");
                    return;
                }
                SendLine("OPEN");
            };
            dropPadItem.Text = "文件拖放区（拖动文件直接传）";
            dropPadItem.Click += delegate { ToggleDropPad(); };
            exitItem.Text = "退出 TypeBridge";
            exitItem.Click += delegate { ExitThread(); };
            trayMenu.Items.Add(toggleItem);
            trayMenu.Items.Add(mouseToggleItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(sendFileItem);
            trayMenu.Items.Add(openRemoteDirItem);
            trayMenu.Items.Add(dropPadItem);
            trayMenu.Items.Add(settingsItem);
            trayMenu.Items.Add(administratorItem);
            trayMenu.Items.Add(exitItem);

            tray.ContextMenuStrip = trayMenu;
            tray.Icon = appIcon;
            tray.DoubleClick += delegate { ToggleKeyboardMode(); };
            tray.Visible = true;

            connectionTimer.Interval = 1000;
            connectionTimer.Tick += delegate { ConnectionTick(); };
            connectionTimer.Start();
            StartFileReceiver();


            clipboardTimer.Interval = 600;
            clipboardTimer.Tick += delegate { PollClipboard(); };
            // 启动时把现有剪贴板当作基线，否则一连上就会把旧内容推给 Ubuntu
            try
            {
                if (Clipboard.ContainsText())
                {
                    lastLocalClip = Clipboard.GetText();
                }
                else
                {
                    lastLocalClip = string.Empty;
                    byte[] baseline = ClipboardImageToPng();
                    if (baseline != null)
                        lastLocalImageKey = ImageKey(baseline, string.Empty);
                }
            }
            catch
            {
                lastLocalClip = string.Empty;
            }
            clipboardTimer.Start();

            ApplyHotkeys();
            SetKeyboardMode(false, false);
            SetMouseMode(false, false);
            ShowHotkeyTip();
            if (!configLoaded)
            {
                // 首次运行：等消息循环真正跑起来再弹设置窗口。
                // 构造函数里直接 ShowDialog 或 BeginInvoke 在部分环境下不会显示。
                Timer firstRunTimer = new Timer();
                firstRunTimer.Interval = 250;
                firstRunTimer.Tick += delegate
                {
                    firstRunTimer.Stop();
                    firstRunTimer.Dispose();
                    ShowSettings(true);
                };
                firstRunTimer.Start();
            }
        }

        private void RestartAsAdministrator()
        {
            try
            {
                using (System.Diagnostics.Process child = System.Diagnostics.Process.Start(
                    AdministratorMode.RestartInfo(Application.ExecutablePath)))
                {
                    if (child != null) ExitThread();
                }
            }
            catch (Win32Exception error)
            {
                // Cancelling UAC leaves the current instance running.
                if (error.NativeErrorCode != 1223)
                {
                    Log.Write("administrator restart failed: " + error.Message);
                    ShowTrayTip("管理员模式启动失败", error.Message);
                }
            }
        }

        private void ShowHotkeyTip()
        {
            string text = linkMouse
                ? keyboardHotkey + " 切换键盘 + 鼠标到 Ubuntu"
                : "键盘 " + keyboardHotkey + "，鼠标 " + mouseHotkey;
            ShowTrayTip("TypeBridge 已启动", text);

            if (!hotkeyForm.KeyboardHotKeyAvailable)
                ShowTrayTip("快捷键不可用", keyboardHotkey + " 已被其他程序占用，请改设置");
            else if (!linkMouse && !hotkeyForm.MouseHotKeyAvailable)
                ShowTrayTip("鼠标快捷键不可用", mouseHotkey + " 已被其他程序占用，请改设置");
        }

        private void ApplyHotkeys()
        {
            hook.ToggleHotkey = keyboardHotkey;
            // 键鼠绑定模式下鼠标热键没有独立意义，交给钩子反而会误触发
            hook.MouseHotkey = linkMouse ? null : mouseHotkey;
            hotkeyForm.Configure(keyboardHotkey, mouseHotkey, !linkMouse);
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                Icon embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null)
                    return embedded;
            }
            catch { }

            try
            {
                string iconPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "assets", "typebridge.ico");
                if (System.IO.File.Exists(iconPath))
                    return new Icon(iconPath);
            }
            catch { }

            return SystemIcons.Application;
        }

        private void LoadConfiguration()
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "typebridge.ini");
            if (!System.IO.File.Exists(path))
                return;

            configLoaded = true;
            try
            {
                foreach (string rawLine in System.IO.File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                        continue;
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                        continue;
                    string key = line.Substring(0, separator).Trim().ToLowerInvariant();
                    string value = line.Substring(separator + 1).Trim();
                    if (key == "ip" && value.Length > 0)
                    {
                        ip = value;
                        RefreshHosts();
                    }
                    if (key == "port")
                    {
                        int configuredPort;
                        if (int.TryParse(value, out configuredPort) && configuredPort > 0 && configuredPort <= 65535)
                            port = configuredPort;
                    }
                    if (key == "mouse_hz")
                    {
                        int configuredHz;
                        if (int.TryParse(value, out configuredHz) && configuredHz >= 50 && configuredHz <= 1000)
                            mouseHz = configuredHz;
                    }
                    if (key == "file_port")
                    {
                        int configuredFilePort;
                        if (int.TryParse(value, out configuredFilePort) && configuredFilePort > 0 && configuredFilePort <= 65535)
                            filePort = configuredFilePort;
                    }
                    if (key == "keyboard_hotkey")
                        keyboardHotkey = Hotkey.Parse(value, keyboardHotkey);
                    if (key == "mouse_hotkey")
                        mouseHotkey = Hotkey.Parse(value, mouseHotkey);
                    if (key == "mouse_with_keyboard")
                        linkMouse = ParseBool(value, linkMouse);
                    if (key == "edge_switch")
                        edgeSwitch = ParseBool(value, edgeSwitch);
                    if (key == "switch_edge")
                    {
                        string lower = value.Trim().ToLowerInvariant();
                        if (lower == "left" || lower == "right" || lower == "top" || lower == "bottom")
                            switchEdge = lower;
                    }
                    if (key == "layout_offset")
                        layoutOffset = value.Trim();
                    if (key == "switch_toast")
                        switchToast = ParseBool(value, switchToast);
                    if (key == "file_port")
                    {
                        int parsed;
                        if (int.TryParse(value, out parsed) && parsed > 0 && parsed <= 65535)
                            filePort = parsed;
                    }
                    if (key == "file_recv_port")
                    {
                        int parsed;
                        if (int.TryParse(value, out parsed) && parsed > 0 && parsed <= 65535)
                            fileRecvPort = parsed;
                    }
                    if (key == "recv_dir")
                        recvDir = value.Trim();
                    if (key == "remote_dir")
                        remoteDir = value.Trim();
                    if (key == "reverse_enable")
                        reverseEnable = ParseBool(value, reverseEnable);
                }
            }
            catch
            {
                // 配置不可读时继续使用默认值。
            }

            // 摆放位置是方向的唯一事实来源（设置页保存时两者一起写）。
            // 手改 ini 造成分叉时按 layout_offset 重新派生，避免边缘切屏方向错乱。
            double layoutX, layoutY;
            if (LayoutMath.TryParseOffset(layoutOffset, out layoutX, out layoutY))
                switchEdge = LayoutMath.EdgeFromOffset(layoutX, layoutY);
        }

        private bool ParseBool(string value, bool fallback)
        {
            string lower = value.Trim().ToLowerInvariant();
            if (lower == "1" || lower == "true" || lower == "yes" || lower == "on")
                return true;
            if (lower == "0" || lower == "false" || lower == "no" || lower == "off")
                return false;
            return fallback;
        }

        private void ShowSettings(bool firstRun)
        {
            if (settingsOpen)
                return;
            // 设置窗口要用本机键盘，先把反向接管收回来（对端会跟着放开抓取）
            EndRemoteDrive("打开设置", true);
            settingsOpen = true;

            if (System.Windows.Application.Current == null)
            {
                System.Windows.Application wpfApp = new System.Windows.Application();
                wpfApp.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            }
            bool resumeKeyboard = keyboardMode;
            bool resumeMouse = mouseMode;

            // 设置窗口要用本机键盘操作，先释放钩子和全局热键，否则按键会被发去 Ubuntu、
            // 正在使用的组合键也无法录制。
            hook.Enabled = false;
            mouseHook.ReleaseAll();
            mouseHook.Enabled = false;
            mouseHook.RestoreCursor();
            // 设置窗口里要拖方块、点按钮，别让边缘切屏把鼠标抢走
            mouseHook.Watching = false;
            mouseMode = false;
            hotkeyForm.SuspendHotkeys();
            UpdateStatus();

            try
            {
                WpfSettingsWindow form = new WpfSettingsWindow(
                    ip, port, keyboardHotkey, mouseHotkey, linkMouse, edgeSwitch, switchEdge,
                    recvDir, remoteDir, firstRun, layoutOffset, reverseEnable);
                {
                    if (form.ShowDialog() != true)
                        return;

                    ip = form.Host;
                    RefreshHosts();
                    port = form.Port;
                    keyboardHotkey = form.KeyboardHotkey;
                    mouseHotkey = form.MouseHotkey;
                    linkMouse = form.LinkMouse;
                    edgeSwitch = form.EdgeSwitch;
                    switchEdge = form.SwitchEdge;
                    layoutOffset = form.LayoutOffset;
                    recvDir = form.RecvDir ?? string.Empty;
                    remoteDir = form.RemoteDir ?? string.Empty;
                    reverseEnable = form.ReverseEnable;
                    mouseHook.SwitchEdge = switchEdge;

                    if (!SaveConfiguration())
                        return;

                    configLoaded = true;
                    CloseConnection();
                    ShowTrayTip("设置已保存", "正在连接 " + ip + ":" + port);
                    ConnectToServer();
                }
            }
            finally
            {
                ApplyHotkeys();
                if (stream != null)
                {
                    // 取消 / 保存失败：连接还在，立刻恢复原状态
                    mouseHook.Watching = edgeSwitch && !mouseMode;
                    SetKeyboardMode(resumeKeyboard, false);
                    if (linkMouse ? resumeKeyboard : resumeMouse)
                        SetMouseMode(true, false);
                }
                else
                {
                    // 保存成功触发了重连，此刻 stream==null，Set*Mode 会被守卫挡下，
                    // hook.Enabled 会一直停在 false。挂到连接成功回调里恢复。
                    resumeKeyboardAfterReconnect = resumeKeyboard;
                    resumeMouseAfterReconnect = linkMouse ? resumeKeyboard : resumeMouse;
                    mouseHook.Watching = false;
                }
                UpdateStatus();
                settingsOpen = false;
            }
        }

        private bool SaveConfiguration()
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "typebridge.ini");
            try
            {
                System.IO.File.WriteAllLines(
                    path,
                    new string[]
                    {
                        "ip=" + ip,
                        "port=" + port.ToString(),
                        "keyboard_hotkey=" + keyboardHotkey.ToString(),
                        "mouse_hotkey=" + mouseHotkey.ToString(),
                        "mouse_with_keyboard=" + (linkMouse ? "1" : "0"),
                        "edge_switch=" + (edgeSwitch ? "1" : "0"),
                        "switch_edge=" + switchEdge,
                        "layout_offset=" + layoutOffset,
                        "switch_toast=" + (switchToast ? "1" : "0"),
                        "file_port=" + filePort.ToString(),
                        "file_recv_port=" + fileRecvPort.ToString(),
                        "recv_dir=" + recvDir,
                        "remote_dir=" + remoteDir,
                        "reverse_enable=" + (reverseEnable ? "1" : "0"),
                        "mouse_hz=" + mouseHz
                    },
                    new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "无法保存配置文件：\r\n" + path + "\r\n\r\n" + ex.Message,
                    "TypeBridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
        }

        private void ToggleKeyboardMode()
        {
            // 刚被本机真实输入收回（比如按的就是这个热键）：同一次按压别再把它送出去
            if ((DateTime.UtcNow - localTakeoverAt).TotalMilliseconds < 400)
            {
                Log.Write("toggle ignored (just took back by local input)");
                return;
            }
            if (remoteDrive)
            {
                // 正在被 Ubuntu 驱动时，按本机热键的含义是"收回"，不是"送过去"：
                // 否则会把本机按键转给对端，和正在注入的事件缠在一起。
                EndRemoteDrive("本机热键收回", true);
                Log.Write("toggle: take back from ubuntu");
                ShowTrayTip("已收回控制", "键盘鼠标已回到 Windows");
                return;
            }
            // 同一次按键可能被 RegisterHotKey 与低级钩子各识别一次（叠加其它全局钩子时
            // 顺序不确定），两次 toggle 会互相抵消，表现就是"按了没反应"。留个窗口去重。
            DateTime now = DateTime.UtcNow;
            if ((now - lastToggleUtc).TotalMilliseconds < 250)
            {
                Log.Write("toggle ignored (within 250ms)");
                return;
            }
            lastToggleUtc = now;

            bool target = !keyboardMode;
            if (target && stream == null)
            {
                Log.Write("toggle aborted: not connected");
                ShowTrayTip("未切换", "尚未连接 Ubuntu，请检查地址和网络");
                return;
            }
            Log.Write("toggle keyboard -> " + (target ? "Ubuntu" : "Windows")
                + " (linkMouse=" + linkMouse + ")");
            if (linkMouse)
            {
                SetKeyboardMode(target, false);
                SetMouseMode(target, false);
                if (switchToast)
                {
                    if (target)
                        ShowTrayTip("TypeBridge", "键盘 + 鼠标 → Ubuntu，按 " + keyboardHotkey + " 切回");
                    else
                        ShowTrayTip("TypeBridge", "键盘 + 鼠标 → Windows");
                }
            }
            else
            {
                SetKeyboardMode(target, true);
            }
        }

        private void ToggleMouseMode()
        {
            if ((DateTime.UtcNow - localTakeoverAt).TotalMilliseconds < 400)
            {
                Log.Write("mouse toggle ignored (just took back by local input)");
                return;
            }
            if (remoteDrive)
            {
                EndRemoteDrive("本机热键收回", true);
                Log.Write("mouse toggle: take back from ubuntu");
                ShowTrayTip("已收回控制", "键盘鼠标已回到 Windows");
                return;
            }
            if (linkMouse)
                return;
            DateTime now = DateTime.UtcNow;
            if ((now - lastMouseToggleUtc).TotalMilliseconds < 250)
            {
                Log.Write("mouse toggle ignored (within 250ms)");
                return;
            }
            lastMouseToggleUtc = now;
            Log.Write("toggle mouse -> " + (mouseMode ? "Windows" : "Ubuntu"));
            SetMouseMode(!mouseMode, true);
        }

        private void SetKeyboardMode(bool remote, bool notify)
        {
            // 未连接时不接管键盘：否则按键全被吞掉又送不出去，用户连 Windows 都没法用
            if (remote && stream == null)
            {
                if (notify)
                    ShowTrayTip("键盘未切换", "尚未连接 Ubuntu，请检查地址和网络");
                return;
            }
            if (keyboardMode != remote)
                Log.Write("keyboard mode=" + (remote ? "Ubuntu" : "Windows"));
            keyboardMode = remote;
            hook.Enabled = remote;
            UpdateStatus();
            SyncRemoteCursor();
            if (notify && switchToast)
                ShowTrayTip("TypeBridge", remote ? "键盘 → Ubuntu（使用 Ubuntu 输入法）" : "键盘 → Windows");
        }

        private bool SetMouseMode(bool remote, bool notify)
        {
            mouseHook.Connected = stream != null;

            if (remote && stream == null)
            {
                if (notify)
                    ShowTrayTip("鼠标未切换", "尚未连接 Ubuntu，请检查地址和网络");
                return false;
            }

            if (remote)
            {
                mouseHook.ResetAnchor();
                mouseHook.Enabled = true;
                mouseHook.Watching = false;
            }
            else
            {
                mouseHook.ReleaseAll();
                mouseHook.Enabled = false;
                mouseHook.RestoreCursor();
                mouseHook.Watching = edgeSwitch && stream != null;
            }

            mouseMode = remote;
            UpdateStatus();
            SyncRemoteCursor();
            if (notify && switchToast)
                ShowTrayTip("TypeBridge", remote ? "鼠标 → Ubuntu" : "鼠标 → Windows");
            return true;
        }

        /// <summary>
        /// 告诉 Ubuntu 那边指针该不该显示：只有"鼠标和键盘都在 Windows"时才隐藏。
        /// 键盘还留在 Ubuntu（只用键盘的场景）时，Ubuntu 的指针要照常显示可用。
        /// </summary>
        private void SyncRemoteCursor()
        {
            if (stream == null)
                return;
            bool owning = mouseMode || keyboardMode;
            SendLine("CUR " + (owning ? "1" : "0"));
            // 同时告诉对端"本机正在驱动你"：它据此拒绝反向接管，避免两边互抢成环
            SendLine("OWN " + (owning ? "1" : "0"));
        }

        private void UpdateStatus()
        {
            bool wasConnected = mouseHook.Connected;
            mouseHook.Connected = stream != null;
            if (mouseHook.Connected && !wasConnected && mouseMode && mouseHook.Enabled)
                mouseHook.ResetAnchor();

            string latency = stream != null && lastRttMs > 0
                ? "，" + lastRttMs.ToString("0") + "ms" : string.Empty;

            if (remoteDrive)
            {
                tray.Text = "TypeBridge ← Ubuntu（Ubuntu 正在控制本机" + latency + "）";
                toggleItem.Text = "收回键盘鼠标\t" + keyboardHotkey;
                mouseToggleItem.Visible = false;
                return;
            }

            if (linkMouse)
            {
                if (keyboardMode)
                {
                    tray.Text = stream != null
                        ? "TypeBridge → Ubuntu（" + (mouseMode ? "键盘 + 鼠标" : "仅键盘") + "，已连接" + latency + "）"
                        : "TypeBridge → Ubuntu（未连接）";
                    toggleItem.Text = "切换到 Windows\t" + keyboardHotkey;
                }
                else
                {
                    tray.Text = "TypeBridge → Windows";
                    toggleItem.Text = "切换到 Ubuntu（键盘 + 鼠标）\t" + keyboardHotkey;
                }
                mouseToggleItem.Visible = false;
            }
            else
            {
                tray.Text = "TypeBridge｜" + (stream != null ? "" : "未连接 ")
                    + "键盘:" + (keyboardMode ? "Ubuntu" : "Windows")
                    + " 鼠标:" + (mouseMode ? "Ubuntu" : "Windows") + latency;
                toggleItem.Text = (keyboardMode ? "键盘切换到 Windows\t" : "键盘切换到 Ubuntu\t")
                    + keyboardHotkey;
                mouseToggleItem.Visible = true;
                mouseToggleItem.Text = (mouseMode ? "鼠标切换到 Windows\t" : "鼠标切换到 Ubuntu\t")
                    + mouseHotkey;
            }
        }

        private void ShowTrayTip(string title, string text)
        {
            toast.ShowMessage(title, text);
        }

        private void HookKey(object sender, BridgeKeyEventArgs e)
        {
            if (!keyboardMode)
                return;
            if (e.Text != null)
                SendText(e.Text);
            else if (!string.IsNullOrEmpty(e.KeyName))
                SendLine("K " + e.KeyName);
        }

        private void HookMouseMove(object sender, BridgeMouseMoveEventArgs e)
        {
            if (!mouseMode)
                return;
            SendLine("M " + e.Dx.ToString() + " " + e.Dy.ToString());
        }

        private void HookMouseButton(object sender, BridgeMouseButtonEventArgs e)
        {
            if (!mouseMode)
                return;
            SendLine("B " + e.Button.ToString() + (e.Down ? " down" : " up"));
        }

        private void HookMouseWheel(object sender, BridgeMouseWheelEventArgs e)
        {
            if (!mouseMode)
                return;
            SendLine("W " + e.Direction);
        }

        // ---------- 边缘切屏 ----------

        private static int MapRange(int value, int fromSize, int toSize, int toOrigin)
        {
            if (fromSize <= 0)
                return toOrigin;
            double ratio = value / (double)fromSize;
            int mapped = toOrigin + (int)Math.Round(ratio * toSize);
            if (mapped < toOrigin)
                mapped = toOrigin;
            if (mapped > toOrigin + toSize - 1)
                mapped = toOrigin + toSize - 1;
            return mapped;
        }

        private void OnEdgeEntered()
        {
            // 反向接管期间本机鼠标只做"收回"这一件事，不能再从边缘切出去
            if (!edgeSwitch || mouseMode || remoteDrive || stream == null)
                return;

            Rectangle b = Screen.PrimaryScreen.Bounds;
            Point p = Cursor.Position;
            int rx;
            int ry;
            // 原来 right 边只给 rx=1，而切回判定是 vx < 0：鼠标往回轻移
            // 2~3px 就立刻切回，表现就是「切过去马上弹回来，像没切成功」。
            // 12px 仍然太敏感（手一抖就 13px），改成 48px：要回来得真的往回推一段。
            const int margin = 48;
            switch (switchEdge)
            {
                case "left":
                    rx = Math.Max(margin, remoteWidth - margin);
                    ry = MapRange(p.Y - b.Top, b.Height, remoteHeight, 0);
                    break;
                case "top":
                    rx = MapRange(p.X - b.Left, b.Width, remoteWidth, 0);
                    ry = Math.Max(margin, remoteHeight - margin);
                    break;
                case "bottom":
                    rx = MapRange(p.X - b.Left, b.Width, remoteWidth, 0);
                    ry = margin;
                    break;
                default:
                    rx = margin;
                    ry = MapRange(p.Y - b.Top, b.Height, remoteHeight, 0);
                    break;
            }
            EnterRemote(rx, ry, "边缘切屏");
        }

        private void OnEdgeReturned()
        {
            Rectangle b = Screen.PrimaryScreen.Bounds;
            int wx;
            int wy;
            // 原来把光标放回 b.Right - 2，仍压在判定线上：armed 撤掉后光标
            // 一直不满足 away 条件，用户继续往外推也没反应，得先挪开再推回来。
            const int gap = 40;
            switch (switchEdge)
            {
                case "left":
                    wx = b.Left + gap;
                    wy = MapRange(mouseHook.VirtualY, remoteHeight, b.Height, b.Top);
                    break;
                case "top":
                    wx = MapRange(mouseHook.VirtualX, remoteWidth, b.Width, b.Left);
                    wy = b.Top + gap;
                    break;
                case "bottom":
                    wx = MapRange(mouseHook.VirtualX, remoteWidth, b.Width, b.Left);
                    wy = b.Bottom - gap;
                    break;
                default:
                    wx = b.Right - gap;
                    wy = MapRange(mouseHook.VirtualY, remoteHeight, b.Height, b.Top);
                    break;
            }

            if (linkMouse)
                SetKeyboardMode(false, false);
            SetMouseMode(false, false);
            mouseHook.Watching = edgeSwitch && stream != null;
            try { Cursor.Position = new Point(wx, wy); }
            catch { }
        }

        /// <summary>进入 Ubuntu：切换到远端坐标 (rx, ry) 并开始跟踪虚拟位置。</summary>
        private void EnterRemote(int rx, int ry, string reason)
        {
            if (linkMouse)
                SetKeyboardMode(true, false);
            if (!SetMouseMode(true, false))
                return;
            mouseHook.BeginTracking(rx, ry);
            SendLine("P " + rx.ToString() + " " + ry.ToString());
            Log.Write("enter remote: " + reason);
            // 边缘切换是高频动作，不再弹浮层：每次切过去都弹一个"→ Ubuntu"很碍事，
            // 光标出现在哪块屏幕上本身就是最直接的反馈。
        }

        protected override void ExitThreadCore()
        {
            if (!disposed)
            {
                disposed = true;
                connectionTimer.Stop();
                clipboardTimer.Stop();
                receiverRunning = false;
                if (fileListener != null) { try { fileListener.Stop(); } catch { } }
                if (dropPad != null) { dropPad.Close(); dropPad = null; }
                if (fileZone != null) { fileZone.Close(); fileZone = null; }
                if (fileZoneTimer != null) { fileZoneTimer.Stop(); fileZoneTimer.Dispose(); fileZoneTimer = null; }
                StopReader();
                mouseHook.ReleaseAll();
                mouseHook.Dispose();
                hook.Dispose();
                toast.Dispose();
                tray.Visible = false;
                tray.Dispose();
                hotkeyForm.Dispose();
                CloseConnection();
            }
            base.ExitThreadCore();
        }
    }
}
