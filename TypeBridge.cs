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

[assembly: AssemblyTitle("TypeBridge")]
[assembly: AssemblyDescription("Windowless Windows to Ubuntu keyboard and mouse bridge")]
[assembly: AssemblyProduct("TypeBridge")]
[assembly: AssemblyVersion("3.7.8.0")]

namespace TypeBridgeApp
{
    /// <summary>屏幕摆放的公共几何：设置页派生方向与实际切屏必须用同一套公式。</summary>
    internal static class LayoutMath
    {
        internal const double BoxW = 132;
        internal const double BoxH = 82;
        internal const double Gap = 18;

        internal static string EdgeFromOffset(double offsetX, double offsetY)
        {
            double stepX = BoxW + Gap;
            double stepY = BoxH + Gap;
            if (Math.Abs(offsetX) / stepX >= Math.Abs(offsetY) / stepY)
                return offsetX < 0 ? "left" : "right";
            return offsetY < 0 ? "top" : "bottom";
        }

        internal static bool TryParseOffset(string text, out double dx, out double dy)
        {
            dx = 0;
            dy = 0;
            string[] xy = (text ?? string.Empty).Split(',');
            if (xy.Length != 2)
                return false;
            return double.TryParse(xy[0].Trim(), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out dx)
                && double.TryParse(xy[1].Trim(), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out dy);
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        [STAThread]
        private static void Main()
        {
            try
            {
                string[] commandLine = Environment.GetCommandLineArgs();
                if (commandLine.Length >= 3 && commandLine[1] == "--send")
                {
                    // 命令行发文件：给脚本/自动化留的口子，也方便不碰托盘直接传
                    RunCliSend(commandLine);
                    return;
                }

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                {
                    LogException(e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    LogException(e.ExceptionObject as Exception);
                };

                bool created;
                using (System.Threading.Mutex mutex = new System.Threading.Mutex(
                    true, "Local\\TypeBridge-SingleInstance-7C4A", out created))
                {
                    if (!created)
                        return;

                    // 上次可能异常退出，系统光标还停在空白图上，启动先恢复
                    GlobalMouseHook.RestoreSystemCursors();
                    // 退出（含 Ctrl+C / 关闭进程）时再兜一次
                    AppDomain.CurrentDomain.ProcessExit += delegate
                    {
                        GlobalMouseHook.RestoreSystemCursors();
                    };
                    // 鼠标钩子拿到的是物理像素坐标，声明 DPI 感知后才能和光标坐标对齐
                    try { SetProcessDPIAware(); } catch { }
                    // WinForms 定时器默认按 15.6ms 计时，提到 1ms 才能跑 250Hz 的位移合并
                    try { timeBeginPeriod(1); } catch { }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (BridgeApplicationContext context = new BridgeApplicationContext())
                    {
                        Application.Run(context);
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                MessageBox.Show(
                    "TypeBridge 启动失败：\r\n" + ex.Message,
                    "TypeBridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                try { timeEndPeriod(1); } catch { }
            }
        }

        /// <summary>把 ini 里的多地址拆成候选列表（逗号 / 分号 / 全角 / 空白）。</summary>
        internal static string[] ParseHostList(string value)
        {
            List<string> list = new List<string>();
            foreach (string part in (value ?? string.Empty).Split(
                new char[] { ',', ';', '\uFF0C', '\uFF1B', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string host = part.Trim();
                if (host.Length > 0 && !list.Contains(host))
                    list.Add(host);
            }
            if (list.Count == 0)
                list.Add("127.0.0.1");
            return list.ToArray();
        }

        /// <summary>
        /// 命令行发文件：TypeBridge.exe --send &lt;文件&gt; [更多文件…]
        /// 给脚本留的口子，也方便不点托盘直接传（不影响正在运行的托盘实例）。
        /// </summary>
        private static void RunCliSend(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string[] cliHosts = new string[] { "127.0.0.1" };
            int port = 4322;
            try
            {
                string ini = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "typebridge.ini");
                if (System.IO.File.Exists(ini))
                {
                    foreach (string raw in System.IO.File.ReadAllLines(ini))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0)
                            continue;
                        string key = raw.Substring(0, eq).Trim().ToLowerInvariant();
                        string value = raw.Substring(eq + 1).Trim();
                        if (key == "ip" && value.Length > 0)
                            cliHosts = Program.ParseHostList(value);
                        else if (key == "file_port")
                        {
                            // TryParse 失败会把 out 置 0，必须先落在临时变量上校验
                            int parsedPort;
                            if (int.TryParse(value, out parsedPort) && parsedPort > 0 && parsedPort <= 65535)
                                port = parsedPort;
                        }
                    }
                }
            }
            catch { }

            string[] files = new string[args.Length - 2];
            Array.Copy(args, 2, files, 0, files.Length);

            int ok = 0;
            string lastDestination = null;
            string firstProblem = null;
            TransferProgressForm progress = new TransferProgressForm();

            progress.Shown += delegate
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    foreach (string file in files)
                    {
                        if (progress.CancelRequested)
                            break;
                        string error;
                        string destination;
                        bool uploaded = false;
                        string lastError = null;
                        // 配置里可以写多个地址，CLI 也要像 GUI 一样逐个尝试，
                        // 不能把含逗号的整串 ip 直接交给 ConnectAsync。
                        foreach (string host in cliHosts)
                        {
                            if (FileSender.Upload(host, port, file, progress, out error, out destination))
                            {
                                uploaded = true;
                                ok++;
                                if (destination != null)
                                    lastDestination = destination;
                                break;
                            }
                            lastError = error;
                        }
                        if (!uploaded && firstProblem == null)
                        {
                            firstProblem = System.IO.Path.GetFileName(file) + "：" + (lastError ?? "发送失败");
                        }
                    }

                    string message = "已发送 " + ok + " / " + files.Length + " 个文件到 Ubuntu";
                    if (ok > 0 && lastDestination != null)
                        message += " → " + lastDestination;
                    if (firstProblem != null)
                        message += "（" + firstProblem + "）";
                    Log.Write("cli send: " + message);

                    progress.Finish(message, false);
                    System.Threading.Thread.Sleep(1500);
                    try { progress.BeginInvoke(new Action(delegate { progress.Close(); })); }
                    catch { }
                });
            };

            progress.BeginTransfer();
            Application.Run(progress);
            Environment.ExitCode = (ok == files.Length && firstProblem == null) ? 0 : 1;
        }

        private static void LogException(Exception ex)
        {
            if (ex == null)
                return;
            try
            {
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "typebridge-error.log");
                System.IO.File.AppendAllText(
                    path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }
    }

    /// <summary>
    /// 轻量诊断日志：记录模式切换、热键命中、连接状态。
    /// 出问题时直接看 typebridge.log 就能定位，不用再靠猜。
    /// 超过 256KB 自动轮换成 .1 一份，不会无限增长。
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 256 * 1024;
        private static readonly object Gate = new object();
        private static readonly Queue<string> Pending = new Queue<string>();
        private static System.Threading.Thread writer;
        private static bool pathBroken;

        /// <summary>
        /// 只入队，立即返回。钩子回调里绝不能碰磁盘——一次 fsync 抖动就够让
        /// 低级钩子超时被系统摘掉，那正是"切不回 Windows"的另一条死路。
        /// </summary>
        internal static void Write(string message)
        {
            if (pathBroken)
                return;
            try
            {
                lock (Gate)
                {
                    if (Pending.Count > 500)
                        Pending.Clear();
                    Pending.Enqueue(DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "  " + message);
                    if (writer != null)
                        return;
                    writer = new System.Threading.Thread(WriterLoop);
                    writer.IsBackground = true;
                    writer.Name = "TypeBridge-Log";
                    writer.Start();
                }
            }
            catch
            {
                pathBroken = true;
            }
        }

        private static void WriterLoop()
        {
            while (true)
            {
                string line = null;
                lock (Gate)
                {
                    if (Pending.Count > 0)
                        line = Pending.Dequeue();
                    else
                        writer = null;
                }
                if (line == null)
                    return;
                try
                {
                    string path = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "typebridge.log");
                    try
                    {
                        System.IO.FileInfo info = new System.IO.FileInfo(path);
                        if (info.Exists && info.Length > MaxBytes)
                        {
                            System.IO.File.Copy(path, path + ".1", true);
                            System.IO.File.WriteAllText(path, string.Empty);
                        }
                    }
                    catch { }
                    System.IO.File.AppendAllText(path, line + "\r\n");
                }
                catch
                {
                    pathBroken = true;
                    return;
                }
            }
        }
    }

    /// <summary>可配置的全局切换快捷键，例如 Ctrl+Alt+U。</summary>
    internal sealed class Hotkey
    {
        private const uint ModAltFlag = 0x0001;
        private const uint ModControlFlag = 0x0002;
        private const uint ModShiftFlag = 0x0004;
        private const uint ModWinFlag = 0x0008;
        private const uint ModNoRepeat = 0x4000;

        internal bool Ctrl { get; private set; }
        internal bool Alt { get; private set; }
        internal bool Shift { get; private set; }
        internal bool Win { get; private set; }
        internal Keys Key { get; private set; }

        internal Hotkey(bool ctrl, bool alt, bool shift, bool win, Keys key)
        {
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
            Win = win;
            Key = key;
        }

        internal static Hotkey DefaultKeyboard()
        {
            return new Hotkey(true, true, false, false, Keys.U);
        }

        internal static Hotkey DefaultMouse()
        {
            return new Hotkey(true, true, false, false, Keys.M);
        }

        internal bool HasModifier
        {
            get { return Ctrl || Alt || Shift || Win; }
        }

        internal uint ModifierFlags
        {
            get
            {
                uint flags = ModNoRepeat;
                if (Ctrl) flags |= ModControlFlag;
                if (Alt) flags |= ModAltFlag;
                if (Shift) flags |= ModShiftFlag;
                if (Win) flags |= ModWinFlag;
                return flags;
            }
        }

        /// <summary>与键盘钩子采集到的修饰键列表和虚拟键码比较。</summary>
        internal bool Matches(List<string> modifiers, int virtualKey)
        {
            if (virtualKey != (int)Key)
                return false;

            int expected = 0;
            if (Ctrl) expected++;
            if (Alt) expected++;
            if (Shift) expected++;
            if (Win) expected++;
            if (modifiers.Count != expected)
                return false;

            if (Ctrl != modifiers.Contains("ctrl")) return false;
            if (Alt != modifiers.Contains("alt")) return false;
            if (Shift != modifiers.Contains("shift")) return false;
            if (Win != modifiers.Contains("super")) return false;
            return true;
        }

        internal static Hotkey Parse(string text, Hotkey fallback)
        {
            if (string.IsNullOrEmpty(text))
                return fallback;

            bool ctrl = false, alt = false, shift = false, win = false;
            Keys key = Keys.None;
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i].Trim();
                if (token.Length == 0)
                    continue;
                string lower = token.ToLowerInvariant();
                if (lower == "ctrl" || lower == "control") { ctrl = true; continue; }
                if (lower == "alt") { alt = true; continue; }
                if (lower == "shift") { shift = true; continue; }
                if (lower == "win" || lower == "super" || lower == "meta") { win = true; continue; }
                key = ParseKey(token);
            }
            if (key == Keys.None)
                return fallback;
            return new Hotkey(ctrl, alt, shift, win, key);
        }

        private static Keys ParseKey(string token)
        {
            string upper = token.ToUpperInvariant();
            if (upper.Length == 1)
            {
                char c = upper[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                    return (Keys)(int)c;
            }
            if (upper.Length >= 2 && upper[0] == 'F')
            {
                int number;
                if (int.TryParse(upper.Substring(1), out number) && number >= 1 && number <= 24)
                    return (Keys)(0x6F + number);
            }
            switch (upper)
            {
                case "SPACE": return Keys.Space;
                case "TAB": return Keys.Tab;
                case "ESC":
                case "ESCAPE": return Keys.Escape;
                case "ENTER":
                case "RETURN": return Keys.Return;
                case "INSERT": return Keys.Insert;
                case "DELETE": return Keys.Delete;
                case "HOME": return Keys.Home;
                case "END": return Keys.End;
                case "PAGEUP":
                case "PRIOR": return Keys.Prior;
                case "PAGEDOWN":
                case "NEXT": return Keys.Next;
                case "UP": return Keys.Up;
                case "DOWN": return Keys.Down;
                case "LEFT": return Keys.Left;
                case "RIGHT": return Keys.Right;
                case "SCROLLLOCK":
                case "SCROLL": return Keys.Scroll;
                case "PAUSE": return Keys.Pause;
                case "MENU":
                case "APPS": return Keys.Apps;
                default: return Keys.None;
            }
        }

        /// <summary>
        /// 可以不带修饰键单独用作切换热键的"整键"。这些键几乎没有软件占用：
        /// 普通键盘只有 F1–F12，没有 F13–F24，所以把 ScrollLock / Pause / Menu
        /// 也放进来——它们在绝大多数键盘上都存在，又是应用程序基本不用的键。
        /// </summary>
        internal static bool IsSafeBareKey(Keys key)
        {
            int code = (int)key;
            if (code >= 0x7C && code <= 0x87)  // F13–F24
                return true;
            return key == Keys.Scroll || key == Keys.Pause || key == Keys.Apps;
        }

        /// <summary>该组合作为全局热键是否合法：要么带修饰键，要么是安全整键。</summary>
        internal bool IsValidHotkey
        {
            get { return HasModifier || IsSafeBareKey(Key); }
        }

        private static string KeyName(Keys key)
        {
            int code = (int)key;
            if (code >= (int)Keys.A && code <= (int)Keys.Z)
                return ((char)code).ToString();
            if (code >= (int)Keys.D0 && code <= (int)Keys.D9)
                return ((char)code).ToString();
            if (code >= 0x70 && code <= 0x87)
                return "F" + (code - 0x6F).ToString();
            switch (key)
            {
                case Keys.Space: return "Space";
                case Keys.Tab: return "Tab";
                case Keys.Escape: return "Esc";
                case Keys.Return: return "Enter";
                case Keys.Insert: return "Insert";
                case Keys.Delete: return "Delete";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.Prior: return "PageUp";
                case Keys.Next: return "PageDown";
                case Keys.Up: return "Up";
                case Keys.Down: return "Down";
                case Keys.Left: return "Left";
                case Keys.Right: return "Right";
                case Keys.Scroll: return "ScrollLock";
                case Keys.Pause: return "Pause";
                case Keys.Apps: return "Menu";
                default: return key.ToString();
            }
        }

        public override string ToString()
        {
            StringBuilder text = new StringBuilder();
            if (Ctrl) text.Append("Ctrl+");
            if (Alt) text.Append("Alt+");
            if (Shift) text.Append("Shift+");
            if (Win) text.Append("Win+");
            text.Append(KeyName(Key));
            return text.ToString();
        }
    }

    internal sealed class HotkeyForm : Form
    {
        private const int WmHotKey = 0x0312;
        private const int KeyboardHotKeyId = 0x5472;
        private const int MouseHotKeyId = 0x5473;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private Hotkey keyboardHotkey = Hotkey.DefaultKeyboard();
        private Hotkey mouseHotkey = Hotkey.DefaultMouse();
        private bool separateMouseHotkey;
        private bool configured;

        internal bool KeyboardHotKeyAvailable { get; private set; }
        internal bool MouseHotKeyAvailable { get; private set; }
        internal event EventHandler KeyboardHotKeyPressed;
        internal event EventHandler MouseHotKeyPressed;

        internal void Configure(Hotkey keyboard, Hotkey mouse, bool separateMouse)
        {
            keyboardHotkey = keyboard;
            mouseHotkey = mouse;
            separateMouseHotkey = separateMouse;
            configured = true;
            if (IsHandleCreated)
                RegisterAll();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (configured)
                RegisterAll();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterAll();
            base.OnHandleDestroyed(e);
        }

        private void UnregisterAll()
        {
            if (KeyboardHotKeyAvailable)
            {
                UnregisterHotKey(Handle, KeyboardHotKeyId);
                KeyboardHotKeyAvailable = false;
            }
            if (MouseHotKeyAvailable)
            {
                UnregisterHotKey(Handle, MouseHotKeyId);
                MouseHotKeyAvailable = false;
            }
        }

        /// <summary>打开设置窗口时释放全局热键，否则录音快捷键会被自己占用。</summary>
        internal void SuspendHotkeys()
        {
            UnregisterAll();
        }

        private void RegisterAll()
        {
            UnregisterAll();
            KeyboardHotKeyAvailable = RegisterHotKey(
                Handle, KeyboardHotKeyId, keyboardHotkey.ModifierFlags, (uint)keyboardHotkey.Key);
            if (separateMouseHotkey)
                MouseHotKeyAvailable = RegisterHotKey(
                    Handle, MouseHotKeyId, mouseHotkey.ModifierFlags, (uint)mouseHotkey.Key);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotKey)
            {
                int id = m.WParam.ToInt32();
                if (id == KeyboardHotKeyId)
                {
                    EventHandler handler = KeyboardHotKeyPressed;
                    if (handler != null)
                        handler(this, EventArgs.Empty);
                    return;
                }
                if (id == MouseHotKeyId)
                {
                    EventHandler handler = MouseHotKeyPressed;
                    if (handler != null)
                        handler(this, EventArgs.Empty);
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>按系统 DPI 换算窗体布局。纯代码创建的窗体不会自动缩放。</summary>
    internal static class UiScale
    {
        internal static readonly float Factor = Detect();

        private static float Detect()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    if (g.DpiX > 0f)
                        return g.DpiX / 96f;
                }
            }
            catch { }
            return 1f;
        }

        internal static int S(int value)
        {
            return (int)Math.Round(value * Factor);
        }
    }

    internal sealed class ToastForm : Form
    {
        private readonly Label titleLabel = new Label();
        private readonly Label messageLabel = new Label();
        private readonly Timer hideTimer = new Timer();

        internal ToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(UiScale.S(380), UiScale.S(84));
            BackColor = Color.FromArgb(30, 30, 30);
            Opacity = 0.96;

            titleLabel.AutoSize = false;
            titleLabel.SetBounds(UiScale.S(16), UiScale.S(10), UiScale.S(348), UiScale.S(20));
            titleLabel.ForeColor = Color.Silver;
            titleLabel.Font = new Font("Microsoft YaHei", 9F);
            titleLabel.Text = "TypeBridge";

            messageLabel.AutoSize = false;
            messageLabel.SetBounds(UiScale.S(16), UiScale.S(32), UiScale.S(348), UiScale.S(38));
            messageLabel.ForeColor = Color.White;
            messageLabel.Font = new Font("Microsoft YaHei", 11F, FontStyle.Bold);
            messageLabel.AutoEllipsis = true;

            hideTimer.Interval = 1400;
            hideTimer.Tick += delegate
            {
                hideTimer.Stop();
                Hide();
            };

            Controls.Add(titleLabel);
            Controls.Add(messageLabel);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        internal void ShowMessage(string title, string message)
        {
            titleLabel.Text = title;
            messageLabel.Text = message;

            Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(
                workArea.Right - Width - UiScale.S(24),
                workArea.Bottom - Height - UiScale.S(24));

            hideTimer.Stop();
            hideTimer.Start();
            if (!Visible)
                Show();
            else
                Invalidate();
        }
    }

    internal sealed class BridgeKeyEventArgs : EventArgs
    {
        internal string Text { get; private set; }
        internal string KeyName { get; private set; }

        internal BridgeKeyEventArgs(string text, string keyName)
        {
            Text = text;
            KeyName = keyName;
        }
    }

    internal sealed class GlobalKeyboardHook : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdData
        {
            internal uint VkCode;
            internal uint ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState,
            [Out] StringBuilder text, int textSize, uint flags, IntPtr layout);

        private readonly HookProc callback;
        private readonly HashSet<int> suppressedKeys = new HashSet<int>();
        private readonly HashSet<int> modifierDown = new HashSet<int>();
        private readonly HashSet<int> modifierUsed = new HashSet<int>();

        // 被我们吞掉过 keydown、但还没等到 keyup 的键。退出接管那一刻 suppressedKeys
        // 会被清空，这个集合故意不清：否则 hotkey 按下后立刻切回 Windows，剩下的
        // 半截 keyup 会漏进当前窗口（部分程序会因此弹出菜单栏）。
        private readonly HashSet<int> swallowUps = new HashSet<int>();
        private readonly Dictionary<int, DateTime> modifierSince = new Dictionary<int, DateTime>();
        private IntPtr hook = IntPtr.Zero;
        private bool enabled;
        private bool capsLockState;

        // 修饰键跟踪时长超过这个值、且硬件状态显示已松开，就判定为"幽灵修饰键"清掉。
        // 阈值必须远大于人手按组合键的间隔，否则会把真的按住误判。
        private static readonly TimeSpan GhostModifierAfter = TimeSpan.FromSeconds(8);

        internal bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                if (value)
                {
                    capsLockState = (GetKeyState(0x14) & 1) != 0;
                }
                else
                {
                    // 退出接管时清空钩子内部状态。残留的 suppressedKeys 会把之后
                    // Windows 模式下的正常按键吞掉（表现为"键盘失灵"），必须清。
                    // swallowUps 与 modifierSince 是例外：它们要活到对应 keyup 到达为止。
                    modifierDown.Clear();
                    modifierUsed.Clear();
                    suppressedKeys.Clear();
                    modifierSince.Clear();
                }
                Log.Write("hook.enabled=" + value);
            }
        }

        internal event EventHandler<BridgeKeyEventArgs> Key;
        internal event EventHandler ToggleRequested;
        internal event EventHandler MouseToggleRequested;

        /// <summary>反向接管期间监听本机真实按键（注入的会被 LLKHF_INJECTED 过滤）。</summary>
        internal bool LocalWatch;
        internal event EventHandler LocalInput;
        private int lastLocalTick;

        /// <summary>Ubuntu 模式下用于切回 Windows 的组合键，与托盘注册的热键保持一致。</summary>
        internal Hotkey ToggleHotkey { get; set; }

        /// <summary>键鼠分离模式下，Ubuntu 模式里也必须能按下鼠标切换热键——
        /// 此时 RegisterHotKey 收不到（按键已被钩子吞掉），只能由钩子识别。</summary>
        internal Hotkey MouseHotkey { get; set; }

        internal GlobalKeyboardHook()
        {
            callback = HookCallback;
            ToggleHotkey = Hotkey.DefaultKeyboard();
        }

        internal void Install()
        {
            if (hook != IntPtr.Zero)
                return;
            hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>
        /// 低级钩子可能被 Windows 静默摘除：回调偶发超时后，系统把它从钩子链里摘掉，
        /// 却不通知进程，句柄值仍然非 0 —— Install() 的幂等保护于是永远挡住重装。
        /// 表现就是进程活着、TCP 正常、看门狗也不报错，但按键和热键都不再触发。
        /// 这里先摘旧句柄再重装，绕过幂等保护；只在非接管态调用，避免打断输入转发。
        /// </summary>
        internal void Reinstall()
        {
            IntPtr old = hook;
            hook = IntPtr.Zero;
            if (old != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(old); }
                catch { }
            }
            Install();
        }

        private static string ModifierName(int virtualKey)
        {
            switch (virtualKey)
            {
                case 0xA0:
                case 0xA1: return "shift";
                case 0xA2:
                case 0xA3: return "ctrl";
                case 0xA4:
                case 0xA5: return "alt";
                case 0x5B:
                case 0x5C: return "super";
                default: return null;
            }
        }

        private static bool IsModifierKey(int virtualKey)
        {
            return ModifierName(virtualKey) != null;
        }

        private static bool IsDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        /// <summary>
        /// 组合键判断用的修饰键集合。以钩子事件跟踪为准，硬件状态只作补充。
        ///
        /// 为什么不能反过来（3.4.0 的 bug）：Ubuntu 模式下修饰键的 keydown 是被我们
        /// 吞掉的，系统异步键状态压根不会记录它——实测在被吞键的钩子里
        /// GetAsyncKeyState(VK_MENU) 恒为 0。于是 "Alt+Z" 永远凑不齐 alt，
        /// 切换热键彻底失效，用户被卡死在 Ubuntu 模式。
        /// </summary>
        private List<string> ActiveModifiers()
        {
            List<string> modifiers = new List<string>();
            if (modifierDown.Contains(0xA2) || modifierDown.Contains(0xA3)
                || IsDown(0xA2) || IsDown(0xA3))
                modifiers.Add("ctrl");
            if (modifierDown.Contains(0xA4) || modifierDown.Contains(0xA5)
                || IsDown(0xA4) || IsDown(0xA5))
                modifiers.Add("alt");
            if (modifierDown.Contains(0xA0) || modifierDown.Contains(0xA1)
                || IsDown(0xA0) || IsDown(0xA1))
                modifiers.Add("shift");
            if (modifierDown.Contains(0x5B) || modifierDown.Contains(0x5C)
                || IsDown(0x5B) || IsDown(0x5C))
                modifiers.Add("super");
            return modifiers;
        }

        /// <summary>
        /// 幽灵修饰键自愈：只有"跟踪时间超过 GhostModifierAfter 且硬件确认已松开"
        /// 才清掉。短时间的硬件状态不可信——那是我们自己吞键造成的，不是真的松手了。
        /// </summary>
        private void ReconcileModifiers()
        {
            // 接管期间修饰键的 keydown/keyup 全被钩子吞掉，GetAsyncKeyState
            // 读不到它们（3.4.1 实测恒为 0）。拿它判"已松开"会把长按超过
            // GhostModifierAfter 的 Ctrl/Shift 误清，组合键降级成单键。
            // 接管时集合完全由钩子事件维护；退出接管时 Enabled setter 已清空。
            if (Enabled || modifierDown.Count == 0)
                return;

            DateTime now = DateTime.UtcNow;
            int[] tracked = new int[modifierDown.Count];
            modifierDown.CopyTo(tracked);
            foreach (int key in tracked)
            {
                DateTime since;
                if (!modifierSince.TryGetValue(key, out since))
                {
                    modifierSince[key] = now;
                    continue;
                }
                if (now - since < GhostModifierAfter || IsDown(key))
                    continue;

                modifierDown.Remove(key);
                modifierUsed.Remove(key);
                suppressedKeys.Remove(key);
                modifierSince.Remove(key);
                Log.Write("ghost modifier cleared: " + key);
            }
        }


        private void MarkModifiersUsed()
        {
            foreach (int key in modifierDown)
                modifierUsed.Add(key);
        }

        /// <summary>吞掉这个键：本次不外泄，同时记进 swallowUps 以便它松手时也吞掉。</summary>
        private IntPtr Swallow(int virtualKey)
        {
            suppressedKeys.Add(virtualKey);
            swallowUps.Add(virtualKey);
            return (IntPtr)1;
        }

        private bool RaiseKey(string text, string keyName)
        {
            EventHandler<BridgeKeyEventArgs> handler = Key;
            if (handler == null)
                return false;
            try
            {
                handler(this, new BridgeKeyEventArgs(text, keyName));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string SpecialKeyName(int virtualKey)
        {
            if (virtualKey >= 0x70 && virtualKey <= 0x87)
                return "F" + (virtualKey - 0x6F).ToString();

            switch ((Keys)virtualKey)
            {
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
                case Keys.CapsLock: return "CapsLock";
                case Keys.NumLock: return "NumLock";
                case Keys.Scroll: return "ScrollLock";
                case Keys.PrintScreen: return "PrintScreen";
                case Keys.Pause: return "Pause";
                case Keys.Apps: return "Menu";
                case Keys.NumPad0: return "KP_0";
                case Keys.NumPad1: return "KP_1";
                case Keys.NumPad2: return "KP_2";
                case Keys.NumPad3: return "KP_3";
                case Keys.NumPad4: return "KP_4";
                case Keys.NumPad5: return "KP_5";
                case Keys.NumPad6: return "KP_6";
                case Keys.NumPad7: return "KP_7";
                case Keys.NumPad8: return "KP_8";
                case Keys.NumPad9: return "KP_9";
                case Keys.Multiply: return "KP_Multiply";
                case Keys.Add: return "KP_Add";
                case Keys.Separator: return "KP_Separator";
                case Keys.Subtract: return "KP_Subtract";
                case Keys.Decimal: return "KP_Decimal";
                case Keys.Divide: return "KP_Divide";
                case Keys.VolumeMute: return "VolumeMute";
                case Keys.VolumeDown: return "VolumeDown";
                case Keys.VolumeUp: return "VolumeUp";
                case Keys.MediaNextTrack: return "MediaNextTrack";
                case Keys.MediaPreviousTrack: return "MediaPrevTrack";
                case Keys.MediaStop: return "MediaStop";
                case Keys.MediaPlayPause: return "MediaPlayPause";
                default: return null;
            }
        }

        private static string BaseKeyName(int virtualKey)
        {
            string special = SpecialKeyName(virtualKey);
            if (special != null)
                return special;

            if (virtualKey >= 0x41 && virtualKey <= 0x5A)
                return ((char)('a' + virtualKey - 0x41)).ToString();
            if (virtualKey >= 0x30 && virtualKey <= 0x39)
                return ((char)('0' + virtualKey - 0x30)).ToString();
            if (virtualKey >= 0x70 && virtualKey <= 0x87)
                return "F" + (virtualKey - 0x6F).ToString();

            switch ((Keys)virtualKey)
            {
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

        private string KeyText(KbdData data)
        {
            byte[] state = new byte[256];
            // Shift 的按下事件已被我们吞掉，GetAsyncKeyState 读不到，只能用自己的跟踪结果
            if (modifierDown.Contains(0xA0) || modifierDown.Contains(0xA1))
                state[0x10] = 0x80;
            if (capsLockState)
                state[0x14] = 0x01;
            if ((GetKeyState(0x90) & 1) != 0)
                state[0x90] = 0x01;

            uint thread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            IntPtr layout = GetKeyboardLayout(thread);
            StringBuilder text = new StringBuilder(8);
            int result = ToUnicodeEx(data.VkCode, data.ScanCode, state, text, text.Capacity, 0, layout);
            if (result <= 0 || text.Length == 0)
                return null;
            foreach (char c in text.ToString())
            {
                if (char.IsControl(c))
                    return null;
            }
            return text.ToString();
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 反向接管期间：本机键盘出现真实按键（不是我们注入的）就说明人回来了，
            // 交给上层收回控制权。放行，不做任何拦截。
            if (nCode >= 0 && LocalWatch && !Enabled)
            {
                int raw = wParam.ToInt32();
                if (raw == WmKeyDown || raw == WmSysKeyDown)
                {
                    KbdData kd = (KbdData)Marshal.PtrToStructure(lParam, typeof(KbdData));
                    if ((kd.Flags & 0x10) == 0)      // LLKHF_INJECTED
                    {
                        int now = Environment.TickCount;
                        if (unchecked(now - lastLocalTick) >= 200)
                        {
                            lastLocalTick = now;
                            EventHandler h = LocalInput;
                            if (h != null)
                            {
                                try { h(this, EventArgs.Empty); }
                                catch { }
                            }
                        }
                    }
                }
            }

            if (nCode >= 0)
            {
                int message = wParam.ToInt32();
                bool down = message == WmKeyDown || message == WmSysKeyDown;
                bool up = message == WmKeyUp || message == WmSysKeyUp;
                if (down || up)
                {
                    KbdData data = (KbdData)Marshal.PtrToStructure(lParam, typeof(KbdData));
                    int vk = (int)data.VkCode;
                    if (up)
                    {
                        // 先取"这个键当初是不是被我们吞掉的"，再决定放不放行。
                        // 退出接管时 suppressedKeys 会被清空，swallowUps 不会，
                        // 这样切换热键的半截 keyup 也不会漏进 Windows。
                        bool swallowed = swallowUps.Remove(vk);
                        modifierSince.Remove(vk);

                        if (IsModifierKey(vk))
                        {
                            bool wasDown = modifierDown.Remove(vk);
                            bool wasUsed = modifierUsed.Remove(vk);
                            suppressedKeys.Remove(vk);
                            if (wasDown && !wasUsed && Enabled)
                            {
                                string modifier = ModifierName(vk);
                                if (modifier != null)
                                    RaiseKey(null, modifier);
                            }
                            if (wasDown || swallowed)
                                return (IntPtr)1;
                            return CallNextHookEx(hook, nCode, wParam, lParam);
                        }
                        if (swallowed || suppressedKeys.Remove(vk))
                            return (IntPtr)1;
                        return CallNextHookEx(hook, nCode, wParam, lParam);
                    }

                    // 新的 keydown 意味着之前的按压状态已经过期，别让陈旧的 swallowUps
                    // 把这一次的 keyup 也吞掉（否则 Windows 会卡住一个键）。
                    swallowUps.Remove(vk);

                    if (!Enabled)
                        return CallNextHookEx(hook, nCode, wParam, lParam);

                    if (vk == (int)Keys.CapsLock)
                    {
                        capsLockState = !capsLockState;
                        return Swallow(vk);
                    }

                    ReconcileModifiers();

                    if (IsModifierKey(vk))
                    {
                        if (modifierDown.Add(vk))
                        {
                            modifierSince[vk] = DateTime.UtcNow;
                            Log.Write("modifier down: " + ModifierName(vk));
                        }
                        return Swallow(vk);
                    }

                    List<string> modifiers = ActiveModifiers();
                    if (ToggleHotkey != null && ToggleHotkey.Matches(modifiers, vk))
                    {
                        MarkModifiersUsed();
                        Log.Write("toggle hotkey hit (hook) modifiers="
                            + string.Join("+", modifiers.ToArray()) + " vk=" + vk);
                        EventHandler toggle = ToggleRequested;
                        if (toggle != null)
                        {
                            try { toggle(this, EventArgs.Empty); }
                            catch { }
                        }
                        return Swallow(vk);
                    }
                    if (MouseHotkey != null && MouseHotkey.Matches(modifiers, vk))
                    {
                        MarkModifiersUsed();
                        Log.Write("mouse hotkey hit (hook) modifiers="
                            + string.Join("+", modifiers.ToArray()) + " vk=" + vk);
                        EventHandler mouseToggle = MouseToggleRequested;
                        if (mouseToggle != null)
                        {
                            try { mouseToggle(this, EventArgs.Empty); }
                            catch { }
                        }
                        return Swallow(vk);
                    }

                    string baseKey = BaseKeyName(vk);
                    string text = null;
                    string keyName = null;

                    if (baseKey != null)
                    {
                        if (modifiers.Count > 0)
                        {
                            keyName = string.Join("+", modifiers.ToArray()) + "+" + baseKey;
                            MarkModifiersUsed();
                        }
                        else
                        {
                            keyName = SpecialKeyName(vk);
                            if (keyName == null)
                                text = KeyText(data);
                        }
                    }
                    else
                    {
                        text = KeyText(data);
                    }

                    if (text != null || keyName != null)
                    {
                        RaiseKey(text, keyName);
                        return Swallow(vk);
                    }
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Enabled = false;
            if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }
    }

    internal sealed class BridgeMouseMoveEventArgs : EventArgs
    {
        internal int Dx { get; private set; }
        internal int Dy { get; private set; }

        internal BridgeMouseMoveEventArgs(int dx, int dy)
        {
            Dx = dx;
            Dy = dy;
        }
    }

    internal sealed class BridgeMouseButtonEventArgs : EventArgs
    {
        internal int Button { get; private set; }
        internal bool Down { get; private set; }

        internal BridgeMouseButtonEventArgs(int button, bool down)
        {
            Button = button;
            Down = down;
        }
    }

    internal sealed class BridgeMouseWheelEventArgs : EventArgs
    {
        internal string Direction { get; private set; }

        internal BridgeMouseWheelEventArgs(string direction)
        {
            Direction = direction;
        }
    }

    /// <summary>
    /// 全局鼠标钩子：Ubuntu 模式下把相对位移/按键/滚轮送到对端，并吞掉本地事件。
    /// 光标的物理位移靠“每次事件后把光标钉回屏幕中心”换算成相对增量，
    /// 这样即使系统把指针夹在屏幕边缘也不会丢位移。
    /// </summary>
    internal sealed class GlobalMouseHook : IDisposable
    {
        private const int WhMouseLl = 14;
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmRButtonDown = 0x0204;
        private const int WmRButtonUp = 0x0205;
        private const int WmMButtonDown = 0x0207;
        private const int WmMButtonUp = 0x0208;
        private const int WmMouseWheel = 0x020A;
        private const int WmXButtonDown = 0x020B;
        private const int WmXButtonUp = 0x020C;
        private const int WmMouseHWheel = 0x020E;
        private const int FlushIntervalMs = 4;
        private const int MaxStep = 2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseData
        {
            internal int X;
            internal int Y;
            internal uint MouseDataRaw;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        // ShowCursor 的显示计数是按线程算的，我们这进程光标底下的窗口属于别人，
        // 减到 -1 也不会真的隐掉（实测 GetCursorInfo 仍是 CURSOR_SHOWING）。
        // 真正全局生效的是把系统光标换成一张全透明图：SetSystemCursor。
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetSystemCursor(IntPtr cursor, uint id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CopyIcon(IntPtr icon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, IntPtr data, uint winIni);

        private const uint SPI_SETCURSORS = 0x0057;

        /// <summary>替换掉这些系统光标：指针可能落在任何窗口上，形状不止一个箭头。</summary>
        private static readonly uint[] CursorIds = new uint[]
        {
            32512, // OCR_NORMAL
            32513, // OCR_IBEAM
            32514, // OCR_WAIT
            32515, // OCR_CROSS
            32516, // OCR_UP
            32642, // OCR_SIZENWSE
            32643, // OCR_SIZENESW
            32644, // OCR_SIZEWE
            32645, // OCR_SIZENS
            32646, // OCR_SIZEALL
            32648, // OCR_NO
            32649, // OCR_HAND
            32650, // OCR_APPSTARTING
        };

        /// <summary>进程启动时先把系统光标恢复成注册表里的默认值：上次若异常退出，光标可能还是空白。</summary>
        internal static void RestoreSystemCursors()
        {
            try { SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        private readonly HookProc callback;
        private readonly Timer flushTimer = new Timer();
        private readonly HashSet<int> pressedButtons = new HashSet<int>();
        private readonly Rectangle screenBounds;
        private IntPtr hook = IntPtr.Zero;
        private int anchorX;
        private int anchorY;
        private int pendingDx;
        private int pendingDy;
        private int restoreX;
        private int restoreY;
        private bool hasRestore;
        private bool pinned;
        private bool cursorHidden;

        private bool enabled;
        private bool connected;

        // 边缘切屏
        private bool watching;
        private bool tracking;
        private bool edgeArmed = true;
        private int edgeHits;
        private int edgeCooldownUntil;
        // 刚切进 Ubuntu 时贴着共享边，要先往屏幕里走一段才允许"再推出去回来"
        private bool edgeReturnArmed;
        private int virtualX;
        private int virtualY;
        private string switchEdge = "right";
        private int remoteWidth = 1920;
        private int remoteHeight = 1080;

        internal bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                UpdateTimer();
            }
        }

        /// <summary>未接管时只观察光标是否撞到屏幕边缘，不拦截任何事件。</summary>
        internal bool Watching
        {
            get { return watching; }
            set
            {
                // 只有「重新开启监视」才重置武装状态。切回 Windows 时
                // EdgeReturned 会把 edgeArmed 置 false，避免光标仍停在边缘
                // 就立刻又切过去；无条件重置会把这个保护每次都抹掉。
                bool wasWatching = watching;
                watching = value;
                if (value && !wasWatching)
                {
                    edgeArmed = true;
                    edgeHits = 0;
                }
                else if (!value)
                {
                    edgeHits = 0;
                }
            }
        }

        /// <summary>切屏方向：光标从这条边出去就进入 Ubuntu。</summary>
        internal string SwitchEdge
        {
            get { return switchEdge; }
            set { switchEdge = value ?? "right"; }
        }

        internal int RemoteWidth
        {
            get { return remoteWidth; }
            set { if (value > 0) remoteWidth = value; }
        }

        internal int RemoteHeight
        {
            get { return remoteHeight; }
            set { if (value > 0) remoteHeight = value; }
        }

        internal event EventHandler EdgeEntered;
        internal event EventHandler EdgeReturned;

        /// <summary>
        /// 反向接管期间（Ubuntu 正在用它的键鼠操作本机）监听本机的真实输入。
        /// 我们自己转发用的 SendInput 事件带 INJECTED 标志，会被过滤掉；
        /// 只有人手真的碰了本机鼠标才算数——那是"回到这台机器"的信号。
        /// </summary>
        internal bool LocalWatch;
        internal event EventHandler LocalInput;
        private int lastLocalInputTick;

        private void DetectLocalInput(IntPtr lParam)
        {
            MouseData data = (MouseData)Marshal.PtrToStructure(lParam, typeof(MouseData));
            if ((data.Flags & 1) != 0)      // LLMHF_INJECTED：我们自己注入的
                return;
            int now = Environment.TickCount;
            if (unchecked(now - lastLocalInputTick) < 200)
                return;
            lastLocalInputTick = now;
            EventHandler handler = LocalInput;
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); }
                catch { }
            }
        }

        /// <summary>按住左键拖到判定线上时触发（用于唤出文件传送区）。</summary>
        internal event EventHandler DragEdgeReached;
        private bool dragEdgeFired;

        internal int VirtualX { get { return virtualX; } }
        internal int VirtualY { get { return virtualY; } }

        /// <summary>接管时用远端屏幕坐标做起点，越界即认为要切回 Windows。</summary>
        internal void BeginTracking(int x, int y)
        {
            virtualX = x;
            virtualY = y;
            tracking = true;
            edgeArmed = false;
            edgeReturnArmed = false;
        }

        /// <summary>
        /// 虚拟光标离「和 Windows 相接的那条边」还有多远（像素，越小越贴边）。
        /// 用来判断"是不是已经走进 Ubuntu 里面了"。
        /// </summary>
        private int InsideDistance()
        {
            switch (switchEdge)
            {
                case "left":
                    return remoteWidth - 1 - virtualX;
                case "top":
                    return remoteHeight - 1 - virtualY;
                case "bottom":
                    return virtualY;
                default:
                    return virtualX;
            }
        }

        internal void StopTracking()
        {
            tracking = false;
        }

        internal bool Connected
        {
            get { return connected; }
            set
            {
                connected = value;
                UpdateTimer();
            }
        }

        internal event EventHandler<BridgeMouseMoveEventArgs> Moved;
        internal event EventHandler<BridgeMouseButtonEventArgs> Button;
        internal event EventHandler<BridgeMouseWheelEventArgs> Wheel;

        internal GlobalMouseHook()
        {
            callback = MouseCallback;
            screenBounds = Screen.PrimaryScreen.Bounds;
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            anchorX = bounds.Left + bounds.Width / 2;
            anchorY = bounds.Top + bounds.Height / 2;
            flushTimer.Interval = FlushIntervalMs;
            flushTimer.Tick += delegate { Flush(); };
        }

        private bool Active
        {
            get { return enabled && connected && hook != IntPtr.Zero; }
        }

        /// <summary>只在「已接管且连接正常」时跑合并定时器，其余时间挂起，避免空转唤醒 CPU。</summary>
        private void UpdateTimer()
        {
            bool shouldRun = Active;
            if (shouldRun == flushTimer.Enabled)
                return;
            if (shouldRun)
            {
                flushTimer.Start();
                return;
            }
            // 停表前把残留位移发出去：此刻 Enabled/Connected 刚被关掉，
            // Active 已是 false，普通 Flush 会先清零再直接 return，最后一小段位移被吞掉。
            Flush(true);
            flushTimer.Stop();
            pendingDx = 0;
            pendingDy = 0;
        }

        /// <summary>位移合并频率。默认 250Hz，可在 typebridge.ini 里用 mouse_hz 配到 1000Hz。</summary>
        internal void SetRate(int hz)
        {
            if (hz < 50)
                hz = 50;
            if (hz > 1000)
                hz = 1000;
            int interval = 1000 / hz;
            if (interval < 1)
                interval = 1;
            flushTimer.Interval = interval;
        }

        internal void Install()
        {
            if (hook != IntPtr.Zero)
                return;
            hook = SetWindowsHookEx(WhMouseLl, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            UpdateTimer();
        }

        /// <summary>同 GlobalKeyboardHook.Reinstall：强制重建被系统静默摘掉的低级鼠标钩子。</summary>
        internal void Reinstall()
        {
            IntPtr old = hook;
            hook = IntPtr.Zero;
            if (old != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(old); }
                catch { }
            }
            Install();
        }

        /// <summary>
        /// 接管期间把本机光标藏起来：把系统光标整体换成 32x32 全透明图。
        /// ShowCursor 对别的窗口无效（计数按线程算），只有换系统光标才是全局的。
        /// </summary>
        private void HideSystemCursor()
        {
            if (cursorHidden)
                return;
            try
            {
                int replaced = 0;
                using (Bitmap blank = new Bitmap(32, 32))
                {
                    IntPtr source = blank.GetHicon();
                    if (source == IntPtr.Zero)
                        return;
                    foreach (uint id in CursorIds)
                    {
                        // SetSystemCursor 会接管并销毁句柄，所以每个 id 都要复制一份
                        IntPtr copy = CopyIcon(source);
                        if (copy != IntPtr.Zero && SetSystemCursor(copy, id))
                            replaced++;
                    }
                    DestroyIcon(source);
                }
                cursorHidden = true;
                Log.Write("hide local cursor (system cursors replaced: " + replaced + ")");
            }
            catch (Exception ex)
            {
                Log.Write("hide local cursor failed: " + ex.Message);
            }
        }

        private void ShowSystemCursor()
        {
            if (!cursorHidden)
                return;
            cursorHidden = false;
            try
            {
                SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
                Log.Write("show local cursor (system cursors restored)");
            }
            catch (Exception ex)
            {
                Log.Write("restore local cursor failed: " + ex.Message);
            }
        }

        /// <summary>接管瞬间把光标钉到屏幕中心，避免锚点贴近屏幕边缘丢位移。</summary>
        internal void ResetAnchor()
        {
            pendingDx = 0;
            pendingDy = 0;
            if (!pinned)
            {
                Point current;
                if (GetCursorPos(out current))
                {
                    restoreX = current.X;
                    restoreY = current.Y;
                    hasRestore = true;
                }
            }
            // 先藏再挪：反过来的话，光标会先"跳"到屏幕中心并露一帧，然后才变透明，
            // 看起来就是闪一下
            HideSystemCursor();
            SetCursorPos(anchorX, anchorY);
            pinned = true;
        }

        /// <summary>退出接管后把光标放回接管前的位置。</summary>
        internal void RestoreCursor()
        {
            if (!pinned)
                return;
            pinned = false;
            pendingDx = 0;
            pendingDy = 0;
            // 先挪到位再显示：否则光标会先在中间露一帧再跳过去
            if (hasRestore)
            {
                SetCursorPos(restoreX, restoreY);
                hasRestore = false;
            }
            ShowSystemCursor();
        }

        /// <summary>退出接管时把还按着的键全部松开，避免对端卡住。</summary>
        internal void ReleaseAll()
        {
            pendingDx = 0;
            pendingDy = 0;
            if (pressedButtons.Count == 0)
                return;
            int[] buttons = new int[pressedButtons.Count];
            pressedButtons.CopyTo(buttons);
            pressedButtons.Clear();
            foreach (int button in buttons)
                RaiseButton(button, false);
        }

        private static int Clamp(int value)
        {
            if (value > MaxStep)
                return MaxStep;
            if (value < -MaxStep)
                return -MaxStep;
            return value;
        }

        private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(hook, nCode, wParam, lParam);

            try
            {
                bool capturing = Active;
                bool watchingEdge = !capturing && watching && connected && hook != IntPtr.Zero;

                if (!capturing && !watchingEdge)
                {
                    pendingDx = 0;
                    pendingDy = 0;
                    pressedButtons.Clear();
                    edgeHits = 0;
                    // 反向接管期间本机鼠标本来是被"让出去"的，但要能收回来：
                    // 人手碰一下本机鼠标就等于说"我回到这台机器了"。
                    // 少了这条，一旦对端没主动交还，本机会一直停在反向接管态，
                    // 鼠标怎么动都没反应（用户实测踩到）。
                    if (LocalWatch)
                        DetectLocalInput(lParam);
                    return CallNextHookEx(hook, nCode, wParam, lParam);
                }

                MouseData data = (MouseData)Marshal.PtrToStructure(lParam, typeof(MouseData));
                int message = wParam.ToInt32();

                if (watchingEdge)
                {
                    if (message == WmMouseMove)
                    {
                        bool leftHeld = (GetAsyncKeyState(0x01) & 0x8000) != 0;
                        bool onEdge = AtSwitchEdgeNow(data.X, data.Y);

                        if (!leftHeld || !onEdge)
                            dragEdgeFired = false;

                        if (leftHeld && onEdge)
                        {
                            // 按住左键拖到判定线：唤出文件传送区，先不切系统
                            if (!dragEdgeFired)
                            {
                                dragEdgeFired = true;
                                EventHandler dragging = DragEdgeReached;
                                if (dragging != null)
                                {
                                    try { dragging(this, EventArgs.Empty); }
                                    catch { }
                                }
                            }
                            return CallNextHookEx(hook, nCode, wParam, lParam);
                        }

                        if (AtSwitchEdge(data.X, data.Y))
                        {
                            EventHandler entered = EdgeEntered;
                            if (entered != null)
                            {
                                try { entered(this, EventArgs.Empty); }
                                catch { }
                            }
                            return (IntPtr)1;
                        }
                    }
                    return CallNextHookEx(hook, nCode, wParam, lParam);
                }

                switch (message)
                {
                    case WmMouseMove:
                    {
                        int dx = data.X - anchorX;
                        int dy = data.Y - anchorY;
                        if (dx != 0 || dy != 0)
                        {
                            if (tracking)
                            {
                                virtualX += dx;
                                virtualY += dy;
                                // 刚切进 Ubuntu 时是贴着共享边进来的（进来点只离边 48px）：
                                // 先得往屏幕里走够 100px 才算就绪，之后往外推才是"要回 Windows"。
                                // 没有这道就绪条件，推过去时手一抖往回带几十像素就会立刻弹回去。
                                if (!edgeReturnArmed && InsideDistance() >= 100)
                                    edgeReturnArmed = true;
                                // 位置判定：光标推到「和 Windows 相接的那条边」就切回，
                                // 和 Windows 自己的多屏切换一致——不要求推得快，推到边上就算。
                                if (edgeReturnArmed
                                    && ShouldReturnToWindows(switchEdge, virtualX, virtualY, remoteWidth, remoteHeight))
                                {
                                    tracking = false;
                                    pendingDx = 0;
                                    pendingDy = 0;
                                    // 先撤掉武装：光标回到屏幕内部才重新 armed。
                                    edgeArmed = false;
                                    edgeCooldownUntil = unchecked(Environment.TickCount + 300);
                                    Log.Write("edge return (vx=" + virtualX + " vy=" + virtualY + ")");
                                    EventHandler returned = EdgeReturned;
                                    if (returned != null)
                                    {
                                        try { returned(this, EventArgs.Empty); }
                                        catch { }
                                    }
                                    return (IntPtr)1;
                                }

                                // 非交接边：夹在远端屏幕范围内，避免坐标越积越远
                                if (virtualX < 0) virtualX = 0;
                                else if (virtualX > remoteWidth - 1) virtualX = remoteWidth - 1;
                                if (virtualY < 0) virtualY = 0;
                                else if (virtualY > remoteHeight - 1) virtualY = remoteHeight - 1;
                            }
                            pendingDx = Clamp(pendingDx + dx);
                            pendingDy = Clamp(pendingDy + dy);
                            // 只在真的动了才钉回锚点，避免 SetCursorPos 自身触发事件形成回环
                            SetCursorPos(anchorX, anchorY);
                        }
                        return (IntPtr)1;
                    }

                    case WmLButtonDown:
                        RaiseButton(1, true);
                        return (IntPtr)1;
                    case WmLButtonUp:
                        RaiseButton(1, false);
                        return (IntPtr)1;

                    case WmRButtonDown:
                        RaiseButton(3, true);
                        return (IntPtr)1;
                    case WmRButtonUp:
                        RaiseButton(3, false);
                        return (IntPtr)1;

                    case WmMButtonDown:
                        RaiseButton(2, true);
                        return (IntPtr)1;
                    case WmMButtonUp:
                        RaiseButton(2, false);
                        return (IntPtr)1;

                    case WmXButtonDown:
                    case WmXButtonUp:
                    {
                        // XBUTTON1 = 后退 -> X11 按钮 8；XBUTTON2 = 前进 -> 按钮 9
                        int which = (int)((data.MouseDataRaw >> 16) & 0xFFFF);
                        RaiseButton(which == 2 ? 9 : 8, message == WmXButtonDown);
                        return (IntPtr)1;
                    }

                    case WmMouseWheel:
                    {
                        int delta = (short)((data.MouseDataRaw >> 16) & 0xFFFF);
                        int notches = delta / 120;
                        if (notches == 0)
                            notches = delta > 0 ? 1 : -1;
                        for (int i = 0; i < Math.Abs(notches) && i < 3; i++)
                            RaiseWheel(notches > 0 ? "up" : "down");
                        return (IntPtr)1;
                    }

                    case WmMouseHWheel:
                    {
                        int delta = (short)((data.MouseDataRaw >> 16) & 0xFFFF);
                        int notches = delta / 120;
                        if (notches == 0)
                            notches = delta > 0 ? 1 : -1;
                        for (int i = 0; i < Math.Abs(notches) && i < 3; i++)
                            RaiseWheel(notches > 0 ? "right" : "left");
                        return (IntPtr)1;
                    }
                }
            }
            catch
            {
                // 钩子回调里绝不能抛异常，否则系统可能直接卸载钩子
            }

            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        /// <summary>
        /// <summary>
        /// 虚拟坐标是否已经越过「和 Windows 相接的那条边」。
        /// left 表示 Ubuntu 在左，交界是 Ubuntu 的右边缘；以此类推。
        /// 这是位置判定：推到边上就切，不看推的速度。
        /// </summary>
        internal static bool ShouldReturnToWindows(string edge, int vx, int vy, int width, int height)
        {
            switch (edge)
            {
                case "left":
                    return vx >= width;
                case "top":
                    return vy >= height;
                case "bottom":
                    return vy < 0;
                default:
                    return vx < 0;
            }
        }

        /// <summary>光标此刻是否正好压在判定线上（拖动时用，不做三次去抖）。</summary>
        private bool AtSwitchEdgeNow(int x, int y)
        {
            switch (switchEdge)
            {
                case "left":
                    return x <= screenBounds.Left + 1;
                case "top":
                    return y <= screenBounds.Top + 1;
                case "bottom":
                    return y >= screenBounds.Bottom - 2;
                default:
                    return x >= screenBounds.Right - 2;
            }
        }

        private bool AtSwitchEdge(int x, int y)
        {
            // 刚从 Ubuntu 推回来的一小段时间内不许再切走，否则光标停在边缘
            // 会来回抖。Watching setter 会把 edgeArmed 重置为 true，所以冷却
            // 必须在这里统一把关，不能只依赖 edgeArmed。
            if (edgeCooldownUntil != 0
                && unchecked(Environment.TickCount - edgeCooldownUntil) < 0)
            {
                edgeHits = 0;
                return false;
            }

            if (!edgeArmed)
            {
                const int margin = 4;
                bool away = x > screenBounds.Left + margin && x < screenBounds.Right - margin - 1
                    && y > screenBounds.Top + margin && y < screenBounds.Bottom - margin - 1;
                if (away)
                    edgeArmed = true;
                return false;
            }

            bool atEdge;
            switch (switchEdge)
            {
                case "left":
                    atEdge = x <= screenBounds.Left;
                    break;
                case "top":
                    atEdge = y <= screenBounds.Top;
                    break;
                case "bottom":
                    atEdge = y >= screenBounds.Bottom - 1;
                    break;
                default:
                    atEdge = x >= screenBounds.Right - 1;
                    break;
            }

            if (!atEdge)
            {
                edgeHits = 0;
                return false;
            }

            edgeHits++;
            // 位置判定：推到判定线上就算数，不再要求连续三拍都在边上。
            // 光标被系统夹在边缘后，系统可能不再产生位置变化，三拍的等待会
            // 变成"看你手抖不抖"——这就是用户说的"有概率才切得过去"。
            edgeHits = 0;
            edgeArmed = false;
            return true;
        }

        private void Flush(bool force = false)
        {
            int dx = pendingDx;
            int dy = pendingDy;
            if (dx == 0 && dy == 0)
                return;
            pendingDx = 0;
            pendingDy = 0;
            if (!force && !Active)
                return;
            EventHandler<BridgeMouseMoveEventArgs> handler = Moved;
            if (handler == null)
                return;
            try
            {
                handler(this, new BridgeMouseMoveEventArgs(dx, dy));
            }
            catch
            {
            }
        }

        private void RaiseButton(int button, bool down)
        {
            if (down)
                pressedButtons.Add(button);
            else
                pressedButtons.Remove(button);
            EventHandler<BridgeMouseButtonEventArgs> handler = Button;
            if (handler == null)
                return;
            try
            {
                handler(this, new BridgeMouseButtonEventArgs(button, down));
            }
            catch
            {
            }
        }

        private void RaiseWheel(string direction)
        {
            EventHandler<BridgeMouseWheelEventArgs> handler = Wheel;
            if (handler == null)
                return;
            try
            {
                handler(this, new BridgeMouseWheelEventArgs(direction));
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Enabled = false;
            Connected = false;
            RestoreCursor();
            // disconnected 状态下 pinned 可能已经是 false，这里再兜一次，
            // 避免异常退出后本机光标一直不可见
            ShowSystemCursor();
            flushTimer.Stop();
            flushTimer.Dispose();
            if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 反向共享的注入端：把 Ubuntu 端发来的事件变成 Windows 上的真实输入
    /// （双向共享里"用 Ubuntu 的键鼠操作这台电脑"那一半）。
    ///
    /// 所有按下的键与按钮都记账：退出、被收回、或对端直接断网时统一松开，
    /// 否则会留下"Ctrl 一直按着""左键一直按着"这类卡死状态。
    /// </summary>
    internal static class RemoteInput
    {
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_XDOWN = 0x0080;
        private const uint MOUSEEVENTF_XUP = 0x0100;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_HWHEEL = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            internal int dx;
            internal int dy;
            internal uint mouseData;
            internal uint dwFlags;
            internal uint time;
            internal IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            internal ushort wVk;
            internal ushort wScan;
            internal uint dwFlags;
            internal uint time;
            internal IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            internal uint uMsg;
            internal ushort wParamL;
            internal ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] internal MOUSEINPUT mi;
            [FieldOffset(0)] internal KEYBDINPUT ki;
            [FieldOffset(0)] internal HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            internal uint type;
            internal INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        /// <summary>键盘虚拟键名表。键名与 Windows → Ubuntu 方向用的是同一套词表。</summary>
        private static readonly Dictionary<string, ushort> VirtualKeys = BuildVirtualKeys();

        /// <summary>需要加 KEYEVENTF_EXTENDEDKEY 的键：方向键、编辑键区、小键盘除号与回车等。</summary>
        private static readonly HashSet<string> ExtendedKeys = BuildExtendedKeys();

        private static readonly object gate = new object();
        private static readonly HashSet<string> downKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<int> downButtons = new HashSet<int>();

        private static Dictionary<string, ushort> BuildVirtualKeys()
        {
            Dictionary<string, ushort> map = new Dictionary<string, ushort>(StringComparer.Ordinal);

            for (char c = 'a'; c <= 'z'; c++)
            {
                map[c.ToString()] = (ushort)(0x41 + (c - 'a'));
                map[char.ToUpperInvariant(c).ToString()] = (ushort)(0x41 + (c - 'a'));
            }
            for (char c = '0'; c <= '9'; c++)
                map[c.ToString()] = (ushort)(0x30 + (c - '0'));
            for (int i = 1; i <= 24; i++)
                map["F" + i.ToString()] = (ushort)(0x6F + i);

            map["Return"] = 0x0D;
            map["KP_Enter"] = 0x0D;
            map["BackSpace"] = 0x08;
            map["Tab"] = 0x09;
            map["Escape"] = 0x1B;
            map["space"] = 0x20;
            map["Prior"] = 0x21;
            map["Next"] = 0x22;
            map["End"] = 0x23;
            map["Home"] = 0x24;
            map["Left"] = 0x25;
            map["Up"] = 0x26;
            map["Right"] = 0x27;
            map["Down"] = 0x28;
            map["PrintScreen"] = 0x2C;
            map["Insert"] = 0x2D;
            map["Delete"] = 0x2E;
            map["Pause"] = 0x13;
            map["CapsLock"] = 0x14;
            map["NumLock"] = 0x90;
            map["ScrollLock"] = 0x91;
            map["Menu"] = 0x5D;

            map["semicolon"] = 0xBA;
            map["equal"] = 0xBB;
            map["comma"] = 0xBC;
            map["minus"] = 0xBD;
            map["period"] = 0xBE;
            map["slash"] = 0xBF;
            map["grave"] = 0xC0;
            map["bracketleft"] = 0xDB;
            map["backslash"] = 0xDC;
            map["bracketright"] = 0xDD;
            map["apostrophe"] = 0xDE;

            map["ctrl"] = 0xA2;    // VK_LCONTROL
            map["shift"] = 0xA0;   // VK_LSHIFT
            map["alt"] = 0xA4;     // VK_LMENU
            map["super"] = 0x5B;   // VK_LWIN

            map["KP_0"] = 0x60;
            map["KP_1"] = 0x61;
            map["KP_2"] = 0x62;
            map["KP_3"] = 0x63;
            map["KP_4"] = 0x64;
            map["KP_5"] = 0x65;
            map["KP_6"] = 0x66;
            map["KP_7"] = 0x67;
            map["KP_8"] = 0x68;
            map["KP_9"] = 0x69;
            map["KP_Multiply"] = 0x6A;
            map["KP_Add"] = 0x6B;
            map["KP_Separator"] = 0x6C;
            map["KP_Subtract"] = 0x6D;
            map["KP_Decimal"] = 0x6E;
            map["KP_Divide"] = 0x6F;

            map["VolumeMute"] = 0xAD;
            map["VolumeDown"] = 0xAE;
            map["VolumeUp"] = 0xAF;
            map["MediaNextTrack"] = 0xB0;
            map["MediaPrevTrack"] = 0xB1;
            map["MediaStop"] = 0xB2;
            map["MediaPlayPause"] = 0xB3;
            return map;
        }

        private static HashSet<string> BuildExtendedKeys()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal)
            {
                "Left", "Right", "Up", "Down", "Prior", "Next", "Home", "End",
                "Insert", "Delete", "NumLock", "PrintScreen", "Menu",
                "KP_Enter", "KP_Divide", "super",
                "VolumeMute", "VolumeDown", "VolumeUp",
                "MediaNextTrack", "MediaPrevTrack", "MediaStop", "MediaPlayPause"
            };
            return set;
        }

        internal static void Key(string name, bool down)
        {
            ushort vk;
            if (string.IsNullOrEmpty(name) || !VirtualKeys.TryGetValue(name, out vk))
                return;
            lock (gate)
            {
                // 对端可能连发 press（自动重复），这里去重，只把状态变化送进系统
                bool changed = down ? downKeys.Add(name) : downKeys.Remove(name);
                if (!changed)
                    return;
                SendKey(vk, ExtendedKeys.Contains(name), down);
            }
        }

        /// <summary>按相对位移移动本机指针。用 SetCursorPos 而不是注入 WM_MOUSEMOVE，
        /// 这样不受"提高指针精确度"的加速曲线影响，两台机器的移动距离是 1:1 的。</summary>
        internal static void MoveBy(int dx, int dy)
        {
            if (dx == 0 && dy == 0)
                return;
            Point current = Cursor.Position;
            int x = current.X + dx;
            int y = current.Y + dy;
            Rectangle screen = SystemInformation.VirtualScreen;
            if (x < screen.Left) x = screen.Left;
            if (x > screen.Right - 1) x = screen.Right - 1;
            if (y < screen.Top) y = screen.Top;
            if (y > screen.Bottom - 1) y = screen.Bottom - 1;
            try { Cursor.Position = new Point(x, y); }
            catch { }
        }

        internal static void Button(int button, bool down)
        {
            uint flag;
            uint data = 0;
            switch (button)
            {
                case 1: flag = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
                case 2: flag = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
                case 3: flag = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
                case 8: flag = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 1; break;
                case 9: flag = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 2; break;
                default: return;
            }
            lock (gate)
            {
                bool changed = down ? downButtons.Add(button) : downButtons.Remove(button);
                if (!changed)
                    return;
                SendMouse(0, 0, data, flag);
            }
        }

        internal static void Wheel(string direction)
        {
            switch ((direction ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "up": SendMouse(0, 0, 120, MOUSEEVENTF_WHEEL); break;
                case "down": SendMouse(0, 0, unchecked((uint)(-120)), MOUSEEVENTF_WHEEL); break;
                case "right": SendMouse(0, 0, 120, MOUSEEVENTF_HWHEEL); break;
                case "left": SendMouse(0, 0, unchecked((uint)(-120)), MOUSEEVENTF_HWHEEL); break;
                default: return;
            }
        }

        /// <summary>松开所有注入过的键与按钮。退出、被收回、断线时都要调用。</summary>
        internal static void ReleaseAll()
        {
            lock (gate)
            {
                foreach (string name in new List<string>(downKeys))
                {
                    ushort vk;
                    if (VirtualKeys.TryGetValue(name, out vk))
                        SendKey(vk, ExtendedKeys.Contains(name), false);
                }
                downKeys.Clear();

                foreach (int button in new List<int>(downButtons))
                {
                    uint flag;
                    uint data = 0;
                    switch (button)
                    {
                        case 1: flag = MOUSEEVENTF_LEFTUP; break;
                        case 2: flag = MOUSEEVENTF_MIDDLEUP; break;
                        case 3: flag = MOUSEEVENTF_RIGHTUP; break;
                        case 8: flag = MOUSEEVENTF_XUP; data = 1; break;
                        case 9: flag = MOUSEEVENTF_XUP; data = 2; break;
                        default: continue;
                    }
                    SendMouse(0, 0, data, flag);
                }
                downButtons.Clear();
            }
        }

        private static void SendKey(ushort vk, bool extended, bool down)
        {
            INPUT input = new INPUT();
            input.type = 1;   // INPUT_KEYBOARD
            input.u.ki.wVk = vk;
            input.u.ki.wScan = 0;
            input.u.ki.dwFlags = (extended ? KEYEVENTF_EXTENDEDKEY : 0u)
                | (down ? 0u : KEYEVENTF_KEYUP);
            input.u.ki.time = 0;
            input.u.ki.dwExtraInfo = IntPtr.Zero;
            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendMouse(int dx, int dy, uint data, uint flags)
        {
            INPUT input = new INPUT();
            input.type = 0;   // INPUT_MOUSE
            input.u.mi.dx = dx;
            input.u.mi.dy = dy;
            input.u.mi.mouseData = data;
            input.u.mi.dwFlags = flags;
            input.u.mi.time = 0;
            input.u.mi.dwExtraInfo = IntPtr.Zero;
            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }
    }

    /// <summary>
    /// 屏幕排列控件：中间是本机，点击上下左右把 Ubuntu 摆到那一侧，
    /// 直观对应"第二台显示器放在哪边"。
    /// </summary>
    internal static class Glass
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            internal int AccentState;
            internal int AccentFlags;
            internal int GradientColor;
            internal int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            internal int Attribute;
            internal IntPtr Data;
            internal int SizeOfData;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        internal static readonly Color WindowColor = Color.FromArgb(242, 245, 251);
        internal static readonly Color CardColor = Color.FromArgb(251, 252, 255);

        internal static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 1 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            int size = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.X, bounds.Y, size, size, 180, 90);
            path.AddArc(bounds.Right - size, bounds.Y, size, size, 270, 90);
            path.AddArc(bounds.Right - size, bounds.Bottom - size, size, size, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - size, size, size, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>给窗口加上真正的背景模糊（Win10/11）。返回是否成功。</summary>
        internal static bool EnableAcrylic(IntPtr handle, Color tint)
        {
            try
            {
                AccentPolicy policy = new AccentPolicy();
                policy.AccentState = 4;   // ACCENT_ENABLE_ACRYLICBLURBEHIND
                // GradientColor 是 0xAABBGGRR
                policy.GradientColor = (tint.A << 24) | (tint.B << 16) | (tint.G << 8) | tint.R;

                int size = Marshal.SizeOf(policy);
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(policy, buffer, false);
                    WindowCompositionAttributeData data = new WindowCompositionAttributeData();
                    data.Attribute = 19;      // WCA_ACCENT_POLICY
                    data.Data = buffer;
                    data.SizeOfData = size;
                    SetWindowCompositionAttribute(handle, ref data);
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>整窗底色：有亚克力时用薄雾，没有时用自带渐变，保证两种情况下都好看。</summary>
        internal static void PaintWindow(Graphics g, Rectangle bounds, bool acrylic)
        {
            if (acrylic)
            {
                using (LinearGradientBrush veil = new LinearGradientBrush(bounds,
                    Color.FromArgb(196, 255, 255, 255), Color.FromArgb(158, 224, 234, 248), 115f))
                {
                    g.FillRectangle(veil, bounds);
                }
            }
            else
            {
                using (LinearGradientBrush back = new LinearGradientBrush(bounds,
                    Color.FromArgb(244, 247, 252), Color.FromArgb(218, 228, 243), 120f))
                {
                    g.FillRectangle(back, bounds);
                }
            }

            // 顶部高光
            Rectangle gloss = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height / 3);
            using (LinearGradientBrush shine = new LinearGradientBrush(
                new Rectangle(gloss.X, gloss.Y - 1, gloss.Width, gloss.Height + 1),
                Color.FromArgb(acrylic ? 70 : 130, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            {
                g.FillRectangle(shine, gloss);
            }
        }

        internal static void PaintInputWells(Graphics g, Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                TextBox box = child as TextBox;
                if (box == null || !box.Visible)
                    continue;
                Rectangle well = new Rectangle(box.Left - 5, box.Top - 4, box.Width + 10, box.Height + 8);
                using (GraphicsPath path = Rounded(well, 8))
                {
                    using (Brush fill = new SolidBrush(Color.FromArgb(238, 255, 255, 255)))
                    {
                        g.FillPath(fill, path);
                    }
                    using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1f))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }
        }

        internal static void PaintCard(Graphics g, Rectangle bounds, int radius, Color top, Color bottom, Color border)
        {
            using (GraphicsPath path = Rounded(bounds, radius))
            {
                using (LinearGradientBrush fill = new LinearGradientBrush(
                    new Rectangle(bounds.X, bounds.Y, bounds.Width, Math.Max(1, bounds.Height + 2)), top, bottom, 90f))
                {
                    g.FillPath(fill, path);
                }
                using (Pen pen = new Pen(border, 1f))
                {
                    g.DrawPath(pen, path);
                }
            }
        }
    }

    /// <summary>
    /// 传文件的进度窗口：文件名、百分比、速度、剩余时间，可取消。
    /// 大文件不显示进度会让人以为卡死，这个窗口就是给"还在动"一个答案。
    /// </summary>
    internal sealed class TransferProgressForm : Form
    {
        private readonly Label titleLabel = new Label();
        private readonly Label percentLabel = new Label();
        private readonly Label detailLabel = new Label();
        private readonly Button cancelButton = new Button();
        internal volatile bool CancelRequested;

        private long totalBytes;
        private long sentBytes;
        private string fileName = string.Empty;
        private DateTime startedUtc;
        private long lastReportTick;
        private double lastRate;
        private bool finished;
        // Finish 的延迟隐藏定时器要能被下一次传输取消
        private Timer hideTimer;

        internal TransferProgressForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(UiScale.S(400), UiScale.S(118));
            BackColor = Color.FromArgb(30, 30, 30);
            Opacity = 0.97;
            DoubleBuffered = true;

            titleLabel.AutoSize = false;
            titleLabel.SetBounds(UiScale.S(16), UiScale.S(10), UiScale.S(300), UiScale.S(20));
            titleLabel.ForeColor = Color.Silver;
            titleLabel.Font = new Font("Microsoft YaHei", 9F);
            titleLabel.Text = "发送到 Ubuntu";

            percentLabel.AutoSize = false;
            percentLabel.SetBounds(UiScale.S(16), UiScale.S(30), UiScale.S(300), UiScale.S(20));
            percentLabel.ForeColor = Color.White;
            percentLabel.Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold);
            percentLabel.Text = "0%";

            detailLabel.AutoSize = false;
            detailLabel.SetBounds(UiScale.S(16), UiScale.S(52), UiScale.S(368), UiScale.S(34));
            detailLabel.ForeColor = Color.Silver;
            detailLabel.Font = new Font("Microsoft YaHei", 8.5F);
            detailLabel.AutoEllipsis = true;

            cancelButton.SetBounds(UiScale.S(322), UiScale.S(8), UiScale.S(62), UiScale.S(26));
            cancelButton.Text = "取消";
            cancelButton.FlatStyle = FlatStyle.Flat;
            cancelButton.FlatAppearance.BorderSize = 1;
            cancelButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
            cancelButton.BackColor = Color.FromArgb(45, 45, 45);
            cancelButton.ForeColor = Color.Gainsboro;
            cancelButton.TabStop = false;
            cancelButton.Click += delegate
            {
                CancelRequested = true;
                cancelButton.Enabled = false;
                detailLabel.Text = "正在取消…";
            };

            Controls.Add(titleLabel);
            Controls.Add(percentLabel);
            Controls.Add(detailLabel);
            Controls.Add(cancelButton);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle bar = new Rectangle(UiScale.S(16), UiScale.S(94),
                ClientSize.Width - UiScale.S(32), UiScale.S(8));
            using (GraphicsPath track = Glass.Rounded(bar, UiScale.S(4)))
            {
                using (Brush fill = new SolidBrush(Color.FromArgb(58, 58, 58)))
                    e.Graphics.FillPath(fill, track);
            }

            double ratio = totalBytes <= 0 ? 0 : Math.Min(1.0, (double)sentBytes / totalBytes);
            int width = (int)(bar.Width * ratio);
            if (width > 0)
            {
                Rectangle done = new Rectangle(bar.X, bar.Y, Math.Max(width, UiScale.S(6)), bar.Height);
                using (GraphicsPath path = Glass.Rounded(done, UiScale.S(4)))
                {
                    using (LinearGradientBrush brush = new LinearGradientBrush(
                        new Rectangle(done.X, done.Y, Math.Max(done.Width, 2), done.Height),
                        Color.FromArgb(96, 176, 255), Color.FromArgb(64, 138, 240), 0f))
                    {
                        e.Graphics.FillPath(brush, path);
                    }
                }
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // 不能抢焦点：传大文件时用户可能还在打字
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return bytes + " B";
            if (bytes < 1024L * 1024)
                return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024)
                return (bytes / 1048576.0).ToString("0.0") + " MB";
            return (bytes / 1073741824.0).ToString("0.00") + " GB";
        }

        internal void BeginTransfer()
        {
            CancelRequested = false;
            finished = false;
            fileName = string.Empty;
            totalBytes = 0;
            sentBytes = 0;
            lastRate = 0;
            lastReportTick = 0;
            startedUtc = DateTime.UtcNow;
            // 上一次 Finish 隐藏了取消按钮并挂了 1.6s 自动隐藏；复用窗体时
            // 两者都要复位，否则取消永远点不到，新传输还会被旧定时器一起隐藏。
            if (hideTimer != null)
            {
                hideTimer.Stop();
                hideTimer.Dispose();
                hideTimer = null;
            }
            cancelButton.Visible = true;
            cancelButton.Enabled = true;
            percentLabel.Text = "准备中…";
            detailLabel.Text = string.Empty;
            titleLabel.Text = "发送到 Ubuntu";
            Invalidate();

            Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            // 比气泡高一点，避免结果气泡盖住进度
            Location = new Point(
                workArea.Right - Width - UiScale.S(24),
                workArea.Bottom - Height - UiScale.S(120));
            if (!Visible)
                Show();
            BringToFront();
        }

        /// <summary>工作线程里调用；内部做线程切换与节流。</summary>
        internal void Report(string name, long sent, long total)
        {
            if (finished)
                return;
            long now = Environment.TickCount;
            if (sent < total && lastReportTick != 0 && now - lastReportTick < 90)
                return;
            lastReportTick = now;

            if (IsDisposed)
                return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(delegate { Report(name, sent, total); })); }
                catch { }
                return;
            }

            fileName = name;
            totalBytes = total;
            sentBytes = sent;

            double seconds = Math.Max(0.001, (DateTime.UtcNow - startedUtc).TotalSeconds);
            double rate = sent / seconds;
            if (rate > 0)
                lastRate = rate;

            int percent = total <= 0 ? 0 : (int)Math.Min(100, sent * 100 / total);
            percentLabel.Text = percent + "%　" + FormatBytes(sent) + " / " + FormatBytes(total);

            string remaining = "—";
            string speed = "—";
            // 共耗时太短时算出来的速率没意义（会显示成几十 GB/s），先按住不显示
            if (seconds > 0.8 && lastRate > 0)
            {
                speed = FormatBytes((long)lastRate) + "/s";
                if (total > sent)
                {
                    double left = (total - sent) / lastRate;
                    if (left < 3600)
                        remaining = TimeSpan.FromSeconds((int)left).ToString(@"mm\:ss");
                }
                else
                {
                    remaining = "00:00";
                }
            }
            detailLabel.Text = name + "\r\n" + speed + "　剩余 " + remaining;
            Invalidate();
        }

        internal void Finish(string message, bool keepVisible)
        {
            finished = true;
            if (IsDisposed)
                return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(delegate { Finish(message, keepVisible); })); }
                catch { }
                return;
            }

            percentLabel.Text = message;
            detailLabel.Text = string.Empty;
            cancelButton.Visible = false;
            Invalidate();

            if (!keepVisible)
            {
                if (hideTimer != null)
                {
                    hideTimer.Stop();
                    hideTimer.Dispose();
                }
                Timer timer = new Timer();
                hideTimer = timer;
                timer.Interval = 1600;
                timer.Tick += delegate
                {
                    timer.Stop();
                    timer.Dispose();
                    if (hideTimer == timer)
                        hideTimer = null;
                    Hide();
                };
                timer.Start();
            }
        }
    }

    internal static class FileSender
    {
        /// <summary>把文件推给 Ubuntu 的文件端口，顺带回调进度。UI 和命令行共用这一份实现。</summary>
        internal static bool Upload(string ip, int port, string path, TransferProgressForm progress,
                                    out string error, out string destination)
        {
            error = null;
            destination = null;
            TcpClient socket = null;
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists)
                {
                    error = "文件不存在";
                    return false;
                }

                socket = new TcpClient();
                socket.NoDelay = true;
                // 大文件传输最怕默默挂死：写超过 60 秒没有进展就判死，让上层拿到明确的错误
                socket.SendTimeout = 60000;
                if (!socket.ConnectAsync(ip, port).Wait(4000))
                {
                    error = "连不上文件端口 " + port;
                    Log.Write("send file failed: " + info.Name + " " + error);
                    return false;
                }

                Log.Write("send file: " + info.Name + " (" + info.Length + " bytes) -> "
                    + ip + ":" + port);

                // 先问对端已经收到多少：新版本回 HAVE <字节数>，旧版本回 ERR bad header，
                // 拿不到就按"从头传"走老格式，保持兼容。
                long offset = 0;
                bool resumeSupported = false;
                try
                {
                    using (TcpClient probe = new TcpClient())
                    {
                        probe.NoDelay = true;
                        if (probe.ConnectAsync(ip, port).Wait(3000))
                        {
                            NetworkStream prober = probe.GetStream();
                            byte[] question = Encoding.UTF8.GetBytes(
                                "RESUME " + info.Length + " " + info.Name + "\n");
                            prober.Write(question, 0, question.Length);
                            prober.Flush();
                            string answer = ReadReply(probe, 2500);
                            if (answer != null && answer.StartsWith("HAVE "))
                            {
                                resumeSupported = true;
                                long already;
                                if (long.TryParse(answer.Substring(5).Trim(), out already)
                                    && already > 0 && already < info.Length)
                                    offset = already;
                            }
                        }
                    }
                }
                catch
                {
                }

                if (offset > 0)
                    Log.Write("resume " + info.Name + " from " + offset + "/" + info.Length
                        + " (" + (offset * 100 / Math.Max(1, info.Length)) + "%)");

                NetworkStream target = socket.GetStream();
                string header = "UPLOAD " + info.Length + " " + info.Name;
                if (resumeSupported)
                    header += " " + offset;
                byte[] head = Encoding.UTF8.GetBytes(header + "\n");
                target.Write(head, 0, head.Length);

                byte[] buffer = new byte[262144];
                long sent = 0;
                using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 262144, FileOptions.SequentialScan))
                {
                    if (offset > 0)
                        input.Seek(offset, SeekOrigin.Begin);
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (progress != null && progress.CancelRequested)
                        {
                            error = "已取消";
                            Log.Write("send file cancelled: " + info.Name + " at "
                                + (offset + sent) + "/" + info.Length + " bytes"
                                + "（已传部分留在 Ubuntu，重发会接着传）");
                            return false;
                        }
                        target.Write(buffer, 0, read);
                        sent += read;
                        if (progress != null)
                            progress.Report(info.Name, offset + sent, info.Length);
                    }
                }
                target.Flush();

                string reply = ReadReply(socket);
                if (reply == null)
                {
                    error = "没有收到 Ubuntu 的确认";
                    Log.Write("send file failed: " + info.Name + " " + error);
                    return false;
                }
                if (!reply.StartsWith("OK"))
                {
                    error = reply;
                    Log.Write("send file failed: " + info.Name + " " + reply);
                    return false;
                }
                // 回复形如 "OK /home/robot/Desktop/x.txt"，把真实落地路径带回去
                destination = reply.Substring(2).Trim();
                Log.Write("sent file: " + info.Name + " (" + info.Length + " bytes) -> " + destination);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Write("send file failed: " + Path.GetFileName(path) + " " + ex.Message);
                return false;
            }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(); }
                    catch { }
                }
            }
        }

        internal static string ReadReply(TcpClient socket, int timeoutMs = 15000)
        {
            try
            {
                socket.ReceiveTimeout = timeoutMs;
                NetworkStream source = socket.GetStream();
                List<byte> bytes = new List<byte>();
                byte[] chunk = new byte[256];
                while (bytes.Count < 4096)
                {
                    int count = source.Read(chunk, 0, chunk.Length);
                    if (count <= 0)
                        break;
                    bool finished = false;
                    for (int i = 0; i < count; i++)
                    {
                        if (chunk[i] == (byte)'\n')
                        {
                            finished = true;
                            break;
                        }
                        bytes.Add(chunk[i]);
                    }
                    if (finished)
                        break;
                }
                if (bytes.Count == 0)
                    return null;
                return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            }
            catch
            {
                return null;
            }
        }
    }

    internal sealed class DropPadForm : Form
    {
        private readonly Action<string[]> onDrop;
        private bool hot;
        private Point dragOrigin;
        private bool draggingWindow;

        internal DropPadForm(Action<string[]> handler)
        {
            onDrop = handler;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(UiScale.S(196), UiScale.S(126));
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            AllowDrop = true;

            using (GraphicsPath shape = Glass.Rounded(new Rectangle(0, 0, Width, Height), UiScale.S(20)))
            {
                Region = new Region(shape);
            }

            DragEnter += delegate(object sender, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effect = DragDropEffects.Copy;
                    hot = true;
                    Invalidate();
                }
                else
                {
                    e.Effect = DragDropEffects.None;
                }
            };

            DragLeave += delegate
            {
                hot = false;
                Invalidate();
            };

            DragDrop += delegate(object sender, DragEventArgs e)
            {
                hot = false;
                Invalidate();
                string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null && paths.Length > 0 && onDrop != null)
                    onDrop(paths);
            };
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle bounds = ClientRectangle;
            using (LinearGradientBrush back = new LinearGradientBrush(bounds,
                Color.FromArgb(250, 252, 255), Color.FromArgb(228, 236, 249), 100f))
            {
                g.FillRectangle(back, bounds);
            }

            Rectangle card = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = Glass.Rounded(card, UiScale.S(19)))
            {
                using (LinearGradientBrush fill = new LinearGradientBrush(
                    new Rectangle(card.X, card.Y, card.Width, card.Height + 1),
                    hot ? Color.FromArgb(255, 250, 252, 255) : Color.FromArgb(235, 252, 253, 255),
                    hot ? Color.FromArgb(255, 226, 238, 254) : Color.FromArgb(205, 236, 242, 251), 90f))
                {
                    g.FillPath(fill, path);
                }
                using (Pen rim = new Pen(Color.FromArgb(210, 255, 255, 255), 1.3f))
                {
                    g.DrawPath(rim, path);
                }
                using (Pen shade = new Pen(Color.FromArgb(52, 96, 122, 158), 1f))
                {
                    g.DrawArc(shade, card.X + 1, card.Y + 1, card.Width - 2, card.Height - 2, 0, 90);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int iconX = Width / 2 - UiScale.S(16);
            int iconY = UiScale.S(22);
            Rectangle paper = new Rectangle(iconX, iconY, UiScale.S(32), UiScale.S(38));
            using (GraphicsPath path = Glass.Rounded(paper, UiScale.S(6)))
            {
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(hot ? 255 : 220, 74, 132, 226)))
                {
                    g.FillPath(fill, path);
                }
            }
            using (Pen line = new Pen(Color.FromArgb(235, 255, 255, 255), 1.6f))
            {
                g.DrawLine(line, paper.Left + UiScale.S(8), paper.Top + UiScale.S(14), paper.Right - UiScale.S(8), paper.Top + UiScale.S(14));
                g.DrawLine(line, paper.Left + UiScale.S(8), paper.Top + UiScale.S(21), paper.Right - UiScale.S(8), paper.Top + UiScale.S(21));
                g.DrawLine(line, paper.Left + UiScale.S(8), paper.Top + UiScale.S(28), paper.Left + UiScale.S(18), paper.Top + UiScale.S(28));
            }

            using (Font f = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold))
            using (Brush b = new SolidBrush(Color.FromArgb(46, 56, 74)))
            {
                TextRenderer.DrawText(g, hot ? "松手就放进 Ubuntu 桌面" : "拖文件到这里", f,
                    new Rectangle(0, UiScale.S(68), Width, UiScale.S(20)),
                    Color.FromArgb(46, 56, 74),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            using (Font f = new Font("Microsoft YaHei UI", 7.8f))
            using (Brush b = new SolidBrush(Color.FromArgb(120, 130, 148)))
            {
                TextRenderer.DrawText(g, "→ Ubuntu 桌面", f,
                    new Rectangle(0, UiScale.S(90), Width, UiScale.S(18)),
                    Color.FromArgb(120, 130, 148),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                draggingWindow = true;
                dragOrigin = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!draggingWindow)
                return;
            Location = new Point(Location.X + e.X - dragOrigin.X, Location.Y + e.Y - dragOrigin.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            draggingWindow = false;
            base.OnMouseUp(e);
        }
    }

    /// <summary>
    /// 按住左键拖到判定线时弹出的玻璃传送区。不抢焦点，这样拖动中的拖放仍能落到它身上。
    /// </summary>
    internal sealed class FileZoneForm : Form
    {
        private readonly Action<string[]> onDrop;
        private bool hot;

        internal FileZoneForm(Action<string[]> handler)
        {
            onDrop = handler;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(UiScale.S(340), UiScale.S(128));
            DoubleBuffered = true;
            AllowDrop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            using (GraphicsPath shape = Glass.Rounded(new Rectangle(0, 0, Width, Height), UiScale.S(22)))
            {
                Region = new Region(shape);
            }

            DragEnter += delegate(object sender, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effect = DragDropEffects.Copy;
                    hot = true;
                    Invalidate();
                }
                else
                {
                    e.Effect = DragDropEffects.None;
                }
            };
            DragLeave += delegate { hot = false; Invalidate(); };
            DragOver += delegate(object sender, DragEventArgs e)
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            DragDrop += delegate(object sender, DragEventArgs e)
            {
                hot = false;
                Invalidate();
                string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null && paths.Length > 0 && onDrop != null)
                    onDrop(paths);
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：别抢走拖动
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进任务栏
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle card = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = Glass.Rounded(card, UiScale.S(21)))
            {
                using (LinearGradientBrush fill = new LinearGradientBrush(
                    new Rectangle(card.X, card.Y, card.Width, card.Height + 1),
                    hot ? Color.FromArgb(255, 244, 249, 255) : Color.FromArgb(238, 250, 252, 255),
                    hot ? Color.FromArgb(255, 214, 232, 255) : Color.FromArgb(208, 234, 241, 252), 90f))
                {
                    g.FillPath(fill, path);
                }
                using (Pen rim = new Pen(Color.FromArgb(215, 255, 255, 255), 1.3f))
                {
                    g.DrawPath(rim, path);
                }
                using (Pen shade = new Pen(Color.FromArgb(55, 96, 122, 158), 1f))
                {
                    g.DrawArc(shade, card.X + 1, card.Y + 1, card.Width - 2, card.Height - 2, 0, 90);
                }
            }

            // 虚线投放框
            Rectangle inner = new Rectangle(UiScale.S(14), UiScale.S(14), Width - UiScale.S(28), Height - UiScale.S(28));
            using (GraphicsPath dash = Glass.Rounded(inner, UiScale.S(14)))
            using (Pen pen = new Pen(Color.FromArgb(150, 74, 132, 226), 1.6f))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, dash);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            string title = hot ? "松手就放进 Ubuntu 桌面" : "拖到这里，放进 Ubuntu 桌面";
            using (Font f = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, title, f, new Rectangle(0, UiScale.S(38), Width, UiScale.S(24)),
                    Color.FromArgb(40, 52, 74),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            using (Font f = new Font("Microsoft YaHei UI", 8F))
            {
                TextRenderer.DrawText(g, "文件会直接出现在下位机桌面", f, new Rectangle(0, UiScale.S(64), Width, UiScale.S(20)),
                    Color.FromArgb(118, 130, 150),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    internal sealed class BridgeApplicationContext : ApplicationContext
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
        private int remoteEdgePush;
        // 刚接管时本机光标就摆在共享边上，要先往屏幕里走一段才允许"再推出去交还"
        private bool remoteArmed;
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
        private volatile Queue<string> pendingLines = new Queue<string>();
        private readonly object sendGate = new object();
        private System.Threading.Thread senderThread;
        private volatile bool senderRunning;
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

        // ---------- 反向共享：Ubuntu 驱动本机 ----------

        /// <summary>
        /// 本机相对 Ubuntu 的方位。switch_edge 记的是「Ubuntu 在本机的哪一侧」，
        /// 反过来就是本机在 Ubuntu 的哪一侧，也是它推指针过来的那条边。
        /// </summary>
        private string UbuntuRelativeSide()
        {
            switch (switchEdge)
            {
                case "left": return "right";
                case "top": return "bottom";
                case "bottom": return "top";
                default: return "left";
            }
        }

        /// <summary>
        /// Ubuntu 端按了它的切换键（或把指针推出了共享边），请求接管这台机器的键鼠。
        /// 先回 ok 再放开注入：对端要等到 ok 才把本机输入真正送过来，两边的状态不会错位。
        /// </summary>
        private void BeginRemoteDrive(int yNorm)
        {
            if (!reverseEnable)
            {
                SendLine("RGW no disabled");
                Log.Write("remote drive denied: disabled in settings");
                return;
            }
            if (settingsOpen)
            {
                SendLine("RGW no busy");
                Log.Write("remote drive denied: settings open");
                return;
            }
            if (remoteDrive)
            {
                // 已经在接管中：再收到一次请求就当确认，别默不作声让对端干等 1.5 秒超时
                SendLine("RGW ok");
                return;
            }

            // 本机正在把键鼠送给 Ubuntu 时对端也可能来接管（人在 Ubuntu 那边推了边缘）。
            // 硬件 KVM 的规则是谁推过去谁说了算，所以这里先让出控制权再接管，
            // 不能回 no busy 把人困在远端。
            // 让出、接管都放到界面线程做：Set*Mode 会动钩子和托盘状态。
            if (keyboardMode || mouseMode)
            {
                Log.Write("remote drive: handing over from local modes");
                try
                {
                    hotkeyForm.BeginInvoke(new Action(delegate
                    {
                        SetKeyboardMode(false, false);
                        SetMouseMode(false, false);
                        StartRemoteDrive(yNorm);
                    }));
                    return;
                }
                catch (Exception ex)
                {
                    Log.Write("remote drive handover failed: " + ex.Message);
                }
            }
            StartRemoteDrive(yNorm);
        }

        /// <summary>真正进入反向接管：摆好指针、置位、回 ok。界面线程和读取线程都会走这里。</summary>
        /// <summary>
        /// 反向接管期间本机出现了真实输入（不是我们注入的）：人回到这台机器了，
        /// 立刻把控制权收回来，并通知 Ubuntu 放开抓取。
        /// </summary>
        private void OnLocalInputDuringRemoteDrive()
        {
            if (!remoteDrive)
                return;
            // 钩子回调里先不直接收尾（会动钩子状态），挪到界面线程做
            try
            {
                hotkeyForm.BeginInvoke(new Action(delegate
                {
                    if (!remoteDrive)
                        return;
                    localTakeoverAt = DateTime.UtcNow;
                    EndRemoteDrive("本机输入", true);
                }));
            }
            catch { }
        }

        private void StartRemoteDrive(int yNorm)
        {
            if (remoteDrive)
                return;

            if (yNorm < 0) yNorm = 0;
            if (yNorm > 65535) yNorm = 65535;

            // 先让本机钩子把手上的键/光标状态收干净，再摆指针：
            // RestoreCursor 会把光标送回接管前的位置，顺序反了会把刚摆好的位置拽走。
            RemoteInput.ReleaseAll();
            mouseHook.ReleaseAll();
            mouseHook.RestoreCursor();
            remoteEdgePush = 0;
            remoteArmed = false;
            remoteDrive = true;
            remoteDriveSince = DateTime.UtcNow;
            // 接管期间关掉本机的边缘侦测：否则真实鼠标蹭到边上会再切去 Ubuntu，两边打架
            mouseHook.Watching = false;
            // 但本机真实输入要能"叫醒"：人手碰一下鼠标/键盘就收回控制权，
            // 否则对端一旦没主动交还，本机就一直停在接管态、鼠标怎么动都没反应。
            mouseHook.LocalWatch = true;
            hook.LocalWatch = true;
            // 接管期间关掉本机的边缘侦测：否则真实鼠标蹭到边上会再切去 Ubuntu，两边打架
            mouseHook.Watching = false;

            // 指针落到共享边上：Ubuntu 那边指针消失的位置，和这里出现的位置对得上
            Rectangle b = Screen.PrimaryScreen.Bounds;
            int x;
            int y;
            switch (switchEdge)
            {
                case "left":
                    x = b.Left + 2;
                    y = MapRange(yNorm, 65535, b.Height, b.Top);
                    break;
                case "top":
                    y = b.Top + 2;
                    x = MapRange(yNorm, 65535, b.Width, b.Left);
                    break;
                case "bottom":
                    y = b.Bottom - 3;
                    x = MapRange(yNorm, 65535, b.Width, b.Left);
                    break;
                default:
                    x = b.Right - 3;
                    y = MapRange(yNorm, 65535, b.Height, b.Top);
                    break;
            }
            try { Cursor.Position = new Point(x, y); }
            catch { }

            SendLine("RGW ok");
            Log.Write("remote drive start (edge=" + switchEdge + ", y=" + yNorm + ")");

            try
            {
                hotkeyForm.BeginInvoke(new Action(delegate
                {
                    UpdateStatus();
                    if (switchToast)
                        ShowTrayTip("TypeBridge", "Ubuntu 正在控制这台电脑，按 " + keyboardHotkey + " 收回");
                }));
            }
            catch { }
        }

        /// <summary>
        /// 结束反向接管。tellUbuntu=true 表示是本机主动收回（热键 / 打开设置 / 边缘切回），
        /// 要通知对端把它的输入抓取放开；对端主动交还时不能回这条，否则两边来回踢。
        /// </summary>
        private void EndRemoteDrive(string reason, bool tellUbuntu)
        {
            bool wasActive = remoteDrive;
            remoteDrive = false;
            mouseHook.LocalWatch = false;
            hook.LocalWatch = false;
            RemoteInput.ReleaseAll();
            if (!wasActive)
                return;

            if (tellUbuntu && stream != null)
                SendLine("RGW 0");
            remoteEdgePush = 0;
            remoteArmed = false;
            mouseHook.Watching = edgeSwitch && stream != null && !mouseMode;
            // 交还时本机光标往往还压在共享边上：真实鼠标一动就会又被边缘切走，
            // 表现成「刚交还就自己切回 Ubuntu」。主动挪进来一点，和切回逻辑一致。
            try
            {
                Rectangle b = Screen.PrimaryScreen.Bounds;
                Point position = Cursor.Position;
                const int gap = 40;
                bool atEdge;
                switch (switchEdge)
                {
                    case "left":
                        atEdge = position.X <= b.Left + 1;
                        if (atEdge) position.X = b.Left + gap;
                        break;
                    case "top":
                        atEdge = position.Y <= b.Top + 1;
                        if (atEdge) position.Y = b.Top + gap;
                        break;
                    case "bottom":
                        atEdge = position.Y >= b.Bottom - 2;
                        if (atEdge) position.Y = b.Bottom - gap;
                        break;
                    default:
                        atEdge = position.X >= b.Right - 2;
                        if (atEdge) position.X = b.Right - gap;
                        break;
                }
                if (atEdge)
                    Cursor.Position = position;
            }
            catch { }
            Log.Write("remote drive end: " + reason);
            try
            {
                hotkeyForm.BeginInvoke(new Action(delegate { UpdateStatus(); }));
            }
            catch { }
        }

        /// <summary>
        /// 反向接管期间的鼠标位移。除了移动指针对，还要盯着"顶到共享边还继续外推"——
        /// 那是在示意把控制权交回 Ubuntu，和 Windows → Ubuntu 方向的边缘切屏对称。
        /// </summary>
        private void OnRemoteMouseMove(int dx, int dy)
        {
            RemoteInput.MoveBy(dx, dy);
            if (!edgeSwitch)
                return;
            // 刚接管的头 400ms 不做"外推交还"判定：对端进入时会把它自己的指针挪到屏幕中心，
            // 那次 warp 的位移偶尔会和人的动作抢顺序，算出一个几百像素的假位移，
            // 瞬间把光标甩到边缘 → 刚切过来就切回去。给一段静默期兜住。
            if ((DateTime.UtcNow - remoteDriveSince).TotalMilliseconds < 400)
                return;

            Rectangle b = Screen.PrimaryScreen.Bounds;
            Point p = Cursor.Position;
            // 接管时指针就摆在共享边上：先往屏幕里走够 80px 才算就绪。
            // 少了这道就绪条件，推过来时手还在往外带（或随手往回拨一下），
            // 几十像素就凑满阈值 -> 立刻交还，表现就是「刚切过去就弹回来」。
            int inside;
            switch (switchEdge)
            {
                case "left":
                    inside = p.X - b.Left;
                    break;
                case "top":
                    inside = p.Y - b.Top;
                    break;
                case "bottom":
                    inside = b.Bottom - 1 - p.Y;
                    break;
                default:
                    inside = b.Right - 1 - p.X;
                    break;
            }
            if (!remoteArmed)
            {
                remoteEdgePush = 0;
                if (inside >= 80)
                    remoteArmed = true;
                return;
            }
            bool pushing;
            switch (switchEdge)
            {
                case "left":
                    pushing = p.X <= b.Left && dx < 0;
                    break;
                case "top":
                    pushing = p.Y <= b.Top && dy < 0;
                    break;
                case "bottom":
                    pushing = p.Y >= b.Bottom - 1 && dy > 0;
                    break;
                default:
                    pushing = p.X >= b.Right - 1 && dx > 0;
                    break;
            }
            if (!pushing)
            {
                remoteEdgePush = 0;
                return;
            }
            int step = Math.Max(Math.Abs(dx), Math.Abs(dy));
            // 单帧几十像素的跳变只可能来自 warp（对端进入时把指针居中），
            // 不是人手在推；直接丢弃并清零，否则一次跳变就能凑满阈值、刚接管就切回。
            if (step > 60)
            {
                remoteEdgePush = 0;
                return;
            }
            remoteEdgePush += step;
            if (remoteEdgePush < 50)
                return;
            remoteEdgePush = 0;
            ReturnToUbuntuByEdge();
        }

        /// <summary>把控制权交回 Ubuntu，并把它的指针放到共享边内侧，接着就能继续用。</summary>
        private void ReturnToUbuntuByEdge()
        {
            if (!remoteDrive)
                return;

            Rectangle b = Screen.PrimaryScreen.Bounds;
            Point p = Cursor.Position;
            const int gap = 40;
            int ux;
            int uy;
            switch (switchEdge)
            {
                case "left":
                    ux = remoteWidth - gap;
                    uy = MapRange(p.Y - b.Top, b.Height, remoteHeight, 0);
                    break;
                case "top":
                    ux = MapRange(p.X - b.Left, b.Width, remoteWidth, 0);
                    uy = remoteHeight - gap;
                    break;
                case "bottom":
                    ux = MapRange(p.X - b.Left, b.Width, remoteWidth, 0);
                    uy = gap;
                    break;
                default:
                    ux = gap;
                    uy = MapRange(p.Y - b.Top, b.Height, remoteHeight, 0);
                    break;
            }
            SendLine("RP " + ux.ToString() + " " + uy.ToString());
            EndRemoteDrive("边缘切回", true);
            // 本机光标也挪离边缘：否则下一次真实鼠标轻微一动就又判定为外推
            try
            {
                Point local = Cursor.Position;
                switch (switchEdge)
                {
                    case "left": local.X = b.Left + gap; break;
                    case "top": local.Y = b.Top + gap; break;
                    case "bottom": local.Y = b.Bottom - gap; break;
                    default: local.X = b.Right - gap; break;
                }
                Cursor.Position = local;
            }
            catch { }
        }

        // ---------- 剪贴板 ----------

        private void PollClipboard()
        {
            if (stream == null || disposed)
                return;

            // 剪贴板没变就不必每 600ms 把整张位图重新编码成 PNG（4K 截图会明显卡顿）。
            uint seq = GetClipboardSequenceNumber();
            if (seq == lastClipboardSeq)
                return;
            lastClipboardSeq = seq;

            // 远端刚写进来的图文：无论回读能否解码成位图都要消费掉回声标志，
            // 否则标志会残留到下一轮，把用户自己复制的新图当成回声吞掉。
            if (suppressImageEcho)
            {
                suppressImageEcho = false;
                try
                {
                    string echoedText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                    byte[] echoed = ClipboardImageToPng();
                    if (echoed != null)
                        lastLocalImageKey = ImageKey(echoed, echoedText);
                }
                catch { }
                return;
            }

            string text = string.Empty;
            byte[] image = null;
            try
            {
                // 图文一起同步：浏览器"复制图片"、Excel 复制区域都是位图 + 文本，
                // 只发其中一种，另一端就粘不出用户真正想要的东西。
                if (Clipboard.ContainsText())
                    text = Clipboard.GetText();
                image = ClipboardImageToPng();
            }
            catch
            {
                return;
            }

            if (image != null)
            {
                string key = ImageKey(image, text);
                if (key == lastLocalImageKey)
                    return;
                lastLocalImageKey = key;
                lastLocalClip = text;
                Log.Write("clipboard image -> ubuntu (" + image.Length + " bytes, text "
                    + text.Length + " chars)");
                byte[] payload = image;
                string caption = text;
                System.Threading.ThreadPool.QueueUserWorkItem(
                    delegate { UploadClipboardImage(payload, caption); });
                return;
            }

            if (text == lastLocalClip)
                return;
            lastLocalClip = text;
            if (text.Length == 0 || text == lastRemoteClip)
                return;
            lastLocalImageKey = null;
            SendLine("C " + json.Serialize(text));
        }

        /// <summary>优先取 CF_DIB/位图；有些程序只往剪贴板放注册格式 PNG。</summary>
        private static byte[] ClipboardImageToPng()
        {
            try
            {
                using (Image image = Clipboard.GetImage())
                {
                    if (image != null)
                    {
                        using (MemoryStream buffer = new MemoryStream())
                        {
                            image.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
                            return buffer.ToArray();
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data != null && data.GetDataPresent("PNG"))
                {
                    MemoryStream stream = data.GetData("PNG") as MemoryStream;
                    if (stream != null)
                        return stream.ToArray();
                    byte[] raw = data.GetData("PNG") as byte[];
                    if (raw != null)
                        return raw;
                }
            }
            catch
            {
            }
            return null;
        }

        private static string ImageKey(byte[] png, string text)
        {
            return Hash(png) + "#" + Hash(Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        private static string Hash(byte[] data)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(data);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>
        /// 把剪贴板图片（以及可选的文本）按 CLIPIMG 协议推给 Ubuntu 的文件端口，不落盘。
        /// 报文：CLIPIMG &lt;图片字节数&gt; &lt;文本字节数&gt;\n + PNG + 文本。
        /// 文本放在后面，旧版守护进程只读图片部分也能正常工作。
        /// </summary>
        private void UploadClipboardImage(byte[] png, string text)
        {
            if (png == null || png.Length == 0)
                return;
            if (png.Length > MaxClipImageBytes)
            {
                Log.Write("clipboard image too large, skipped: " + png.Length + " bytes");
                return;
            }
            if (System.Threading.Interlocked.CompareExchange(ref imageSending, 1, 0) != 0)
                return;   // 上一张还在传，这张直接跳过，避免堆积

            byte[] caption = Encoding.UTF8.GetBytes(text ?? string.Empty);
            TcpClient socket = null;
            try
            {
                socket = new TcpClient();
                socket.NoDelay = true;
                string[] fileHosts = FileTargetHosts();
                if (!socket.ConnectAsync(fileHosts[0], filePort).Wait(4000))
                {
                    Log.Write("clipboard image: 连不上文件端口 " + filePort);
                    return;
                }

                NetworkStream target = socket.GetStream();
                byte[] head = Encoding.UTF8.GetBytes("CLIPIMG " + png.Length + " " + caption.Length + "\n");
                target.Write(head, 0, head.Length);
                target.Write(png, 0, png.Length);
                if (caption.Length > 0)
                    target.Write(caption, 0, caption.Length);
                target.Flush();

                string reply = ReadReply(socket);
                Log.Write("clipboard image -> ubuntu: " + (reply ?? "(no reply)"));
            }
            catch (Exception ex)
            {
                Log.Write("clipboard image upload failed: " + ex.Message);
            }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(); }
                    catch { }
                }
                System.Threading.Interlocked.Exchange(ref imageSending, 0);
            }
        }

        private void ApplyRemoteClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            lastRemoteClip = text;
            lastLocalClip = text;
            lastLocalImageKey = null;
            try { Clipboard.SetText(text); }
            catch { }
        }

        private void ApplyRemoteClipboardImage(byte[] png, string text)
        {
            if (png == null || png.Length == 0)
                return;
            try
            {
                // DataObject 会持有 Bitmap 供剪贴板按需序列化，成功时不能 Dispose；
                // 但 SetDataObject 抛异常（剪贴板被别的程序占用很常见）时要释放，
                // 否则每失败一次就泄漏一个 GDI+ 句柄。
                Bitmap image = null;
                bool handedOff = false;
                try
                {
                    using (MemoryStream buffer = new MemoryStream(png))
                    using (Image loaded = Image.FromStream(buffer))
                        image = new Bitmap(loaded);

                    // 图 + 文一起放：粘到图片程序里是图，粘到输入框里是文本
                    DataObject payload = new DataObject();
                    payload.SetData(DataFormats.Bitmap, true, image);
                    if (!string.IsNullOrEmpty(text))
                        payload.SetData(DataFormats.UnicodeText, true, text);
                    Clipboard.SetDataObject(payload, true);
                    handedOff = true;
                }
                finally
                {
                    if (!handedOff && image != null)
                        image.Dispose();
                }

                suppressImageEcho = true;
                lastLocalImageKey = null;
                lastLocalClip = text ?? string.Empty;
                lastRemoteClip = text ?? string.Empty;
                Log.Write("clipboard <- ubuntu image (" + png.Length + " bytes, text "
                    + (text == null ? 0 : text.Length) + " chars)");
            }
            catch (Exception ex)
            {
                Log.Write("set clipboard image failed: " + ex.Message);
            }
        }

        // ---------- 服务端下行消息 ----------

        private void StartReader(NetworkStream target)
        {
            StopReader();
            readerRunning = true;
            lastPongUtc = DateTime.UtcNow;

            // 始终下发：空值表示「用桌面」，否则守护进程会一直沿用上次的旧路径
            SendLine("DIR " + (remoteDir ?? string.Empty).Trim());
            // 上一次可能把对端指针藏起来了，重连先把可见性恢复
            SendLine("CUR 1");
            // 新连接先声明"本机没有在驱动对端"，对端据此决定能不能反向接管
            SendLine("OWN 0");
            // 反向共享用的信息：本机屏幕尺寸 + 本机在 Ubuntu 的哪一侧（决定它从哪条边推过来）
            SendLine("SW " + Screen.PrimaryScreen.Bounds.Width + " "
                + Screen.PrimaryScreen.Bounds.Height + " " + UbuntuRelativeSide());
            readerThread = new System.Threading.Thread(delegate () { ReadLoop(target); });
            readerThread.IsBackground = true;
            readerThread.Start();
        }

        private void StopReader()
        {
            readerRunning = false;
            readerThread = null;
        }

        private void ReadLoop(NetworkStream target)
        {
            byte[] buffer = new byte[8192];
            // 必须在字节层面组帧：一个中文字符的 UTF-8 序列可能被拆到两次 Read，
            // 逐块 GetString 会各自产生 U+FFFD，拼回字符串也救不回来。
            List<byte> pending = new List<byte>();
            while (readerRunning)
            {
                int count;
                try
                {
                    count = target.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    break;
                }
                if (count <= 0)
                    break;

                for (int i = 0; i < count; i++)
                    pending.Add(buffer[i]);
                if (pending.Count > (1 << 20))
                {
                    Log.Write("server line too long, dropping buffer");
                    pending.Clear();
                    continue;
                }

                while (true)
                {
                    int index = pending.IndexOf((byte)'\n');
                    if (index < 0)
                        break;
                    string line = Encoding.UTF8.GetString(pending.ToArray(), 0, index).TrimEnd('\r');
                    pending.RemoveRange(0, index + 1);
                    if (line.Length > 0)
                        HandleServerLine(line);
                }
            }
        }

        private void HandleServerLine(string line)
        {
            if (line == "PONG")
            {
                lastPongUtc = DateTime.UtcNow;
                if (pingSentTs != 0)
                {
                    lastRttMs = (System.Diagnostics.Stopwatch.GetTimestamp() - pingSentTs)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    pingSentTs = 0;
                    // 延迟明显变高时记一笔，方便判断是不是被 TUN / 代理绕远了
                    if (lastRttMs > 120 && Environment.TickCount - lastSlowWarnTick > 20000)
                    {
                        lastSlowWarnTick = Environment.TickCount;
                        Log.Write("network slow: rtt=" + lastRttMs.ToString("0") + "ms");
                    }
                }
                return;
            }
            if (line.Length >= 2 && line[0] == 'S' && line[1] == ' ')
            {
                string[] parts = line.Substring(2).Split(' ');
                int width;
                int height;
                if (parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height)
                    && width > 0 && height > 0)
                {
                    remoteWidth = width;
                    remoteHeight = height;
                    mouseHook.RemoteWidth = width;
                    mouseHook.RemoteHeight = height;
                }
                return;
            }

            // ---------- 反向共享：Ubuntu 的键鼠操作本机 ----------
            // 注入直接在本线程做（少一次界面线程排队，输入延迟更低）；
            // 只有托盘文案、提示浮层才切回 UI 线程。
            string[] fields = line.Split(' ');
            if (fields[0] == "RGW")
            {
                string action = fields.Length > 1 ? fields[1] : string.Empty;
                if (action == "1")
                {
                    int yNorm = 32768;
                    if (fields.Length > 2)
                        int.TryParse(fields[2], out yNorm);
                    BeginRemoteDrive(yNorm);
                }
                else if (action == "0")
                {
                    EndRemoteDrive("Ubuntu 交还控制", false);
                }
                return;
            }
            if (fields[0] == "RK" && fields.Length >= 3)
            {
                if (remoteDrive)
                    RemoteInput.Key(fields[1], fields[2] == "down");
                return;
            }
            if (fields[0] == "RM" && fields.Length >= 3)
            {
                int dx;
                int dy;
                if (remoteDrive && int.TryParse(fields[1], out dx) && int.TryParse(fields[2], out dy))
                    OnRemoteMouseMove(dx, dy);
                return;
            }
            if (fields[0] == "RB" && fields.Length >= 3)
            {
                int button;
                if (remoteDrive && int.TryParse(fields[1], out button))
                    RemoteInput.Button(button, fields[2] == "down");
                return;
            }
            if (fields[0] == "RW" && fields.Length >= 2)
            {
                if (remoteDrive)
                    RemoteInput.Wheel(fields[1]);
                return;
            }

            if (line == "BT")
            {
                // Ubuntu 端托盘里点了「切回 Windows」
                Log.Write("switch back requested by ubuntu tray");
                try
                {
                    hotkeyForm.BeginInvoke(new Action(delegate
                    {
                        SetKeyboardMode(false, true);
                        SetMouseMode(false, true);
                    }));
                }
                catch { }
                return;
            }

            if (line.StartsWith("DIR ", StringComparison.Ordinal))
            {
                // Ubuntu 端收到 DIR 后回的是"它实际会写进去的目录"
                remoteResolvedDir = line.Substring(4).Trim();
                Log.Write("ubuntu receive dir: " + remoteResolvedDir);
                return;
            }

            if (line.StartsWith("OPEN ", StringComparison.Ordinal))
            {
                string result = line.Substring(5).Trim();
                Log.Write("open dir on ubuntu: " + result);
                try
                {
                    hotkeyForm.BeginInvoke(new Action(delegate
                    {
                        ShowTrayTip("Ubuntu 接收目录",
                            result == "ok" ? (remoteResolvedDir.Length > 0 ? remoteResolvedDir : "已在 Ubuntu 上打开")
                                           : ("打开失败：" + result));
                    }));
                }
                catch { }
                return;
            }

            if (line.Length >= 2 && line[0] == 'C' && line[1] == ' ')
            {
                string text = null;
                try { text = json.Deserialize<string>(line.Substring(2)); }
                catch { }
                if (text == null)
                    return;
                try
                {
                    hotkeyForm.BeginInvoke(new Action(delegate { ApplyRemoteClipboard(text); }));
                }
                catch { }
            }
        }

        // ---------- 文件发送 ----------

        // ---------- 文件拖放区与接收 ----------

        /// <summary>按住左键拖到判定线时：在判定线内侧弹出玻璃传送区。</summary>
        private void ShowFileZone()
        {
            if (mouseMode)
                return;

            if (fileZone == null || fileZone.IsDisposed)
                fileZone = new FileZoneForm(delegate (string[] paths) { HideFileZone(); UploadFiles(paths); });

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Point cursor = Cursor.Position;
            int x;
            int y;

            switch (switchEdge)
            {
                case "left":
                    x = area.Left + UiScale.S(28);
                    y = cursor.Y - fileZone.Height / 2;
                    break;
                case "right":
                    x = area.Right - fileZone.Width - UiScale.S(28);
                    y = cursor.Y - fileZone.Height / 2;
                    break;
                case "top":
                    x = cursor.X - fileZone.Width / 2;
                    y = area.Top + UiScale.S(28);
                    break;
                default:
                    x = cursor.X - fileZone.Width / 2;
                    y = area.Bottom - fileZone.Height - UiScale.S(28);
                    break;
            }

            x = Math.Max(area.Left + 8, Math.Min(area.Right - fileZone.Width - 8, x));
            y = Math.Max(area.Top + 8, Math.Min(area.Bottom - fileZone.Height - 8, y));
            fileZone.Location = new Point(x, y);

            if (!fileZone.Visible)
                fileZone.Show();
            fileZone.BringToFront();

            if (fileZoneTimer == null)
            {
                fileZoneTimer = new Timer();
                fileZoneTimer.Interval = 4000;
                fileZoneTimer.Tick += delegate { fileZoneTimer.Stop(); HideFileZone(); };
            }
            fileZoneTimer.Stop();
            fileZoneTimer.Start();
        }

        private void HideFileZone()
        {
            if (fileZoneTimer != null)
                fileZoneTimer.Stop();
            if (fileZone != null && fileZone.Visible)
                fileZone.Hide();
        }

        private void ToggleDropPad()
        {
            if (dropPad == null || dropPad.IsDisposed)
            {
                dropPad = new DropPadForm(delegate (string[] paths) { UploadFiles(paths); });
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                dropPad.Location = new Point(area.Right - dropPad.Width - 32, area.Bottom - dropPad.Height - 32);
            }

            if (dropPad.Visible)
            {
                dropPad.Hide();
            }
            else
            {
                dropPad.Show();
                dropPad.BringToFront();
            }
        }

        private void StartFileReceiver()
        {
            try
            {
                fileListener = new TcpListener(System.Net.IPAddress.Any, fileRecvPort);
                fileListener.Start();
                receiverRunning = true;
                System.Threading.Thread thread = new System.Threading.Thread(FileAcceptLoop);
                thread.IsBackground = true;
                thread.Start();
                Log.Write("file receiver on " + fileRecvPort);
            }
            catch (Exception ex)
            {
                Log.Write("file receiver failed: " + ex.Message);
            }
        }

        private void FileAcceptLoop()
        {
            while (receiverRunning)
            {
                TcpClient incoming = null;
                try
                {
                    incoming = fileListener.AcceptTcpClient();
                }
                catch
                {
                    return;
                }

                TcpClient target = incoming;
                System.Threading.Thread worker = new System.Threading.Thread(delegate () { ReceiveUpload(target); });
                worker.IsBackground = true;
                worker.Start();
            }
        }

        private void ReceiveUpload(TcpClient incoming)
        {
            try
            {
                incoming.ReceiveTimeout = 60000;
                NetworkStream source = incoming.GetStream();

                string header = ReadAsciiLine(source);
                string[] parts = header.Split(new char[] { ' ' }, 3);
                bool clipImage = parts.Length == 3 && parts[0] == "CLIPIMG";
                bool resumeQuery = parts.Length == 3 && parts[0] == "RESUME";
                if (parts.Length != 3 || (parts[0] != "UPLOAD" && !clipImage && !resumeQuery))
                {
                    SendAscii(source, "ERR bad header");
                    return;
                }

                long size;
                if (!long.TryParse(parts[1], out size) || size < 0)
                {
                    SendAscii(source, "ERR bad size");
                    return;
                }

                if (clipImage)
                {
                    // 剪贴板图片：读进内存直接上剪贴板，不落盘。
                    // 第三段是可选文本长度；旧版对端这里放的是文件名，解析失败按 0 处理。
                    long captionLength;
                    if (!long.TryParse(parts[2], out captionLength) || captionLength < 0)
                        captionLength = 0;
                    // 图片和文本都攒在内存里，畸形长度头足以把进程撑爆
                    if (size > MaxClipImageBytes || captionLength > MaxClipCaptionBytes)
                    {
                        SendAscii(source, "ERR clipboard too large");
                        return;
                    }

                    byte[] png;
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        long remainingImage = size;
                        byte[] chunk = new byte[65536];
                        while (remainingImage > 0)
                        {
                            int count = source.Read(chunk, 0, (int)Math.Min(chunk.Length, remainingImage));
                            if (count <= 0)
                                break;
                            buffer.Write(chunk, 0, count);
                            remainingImage -= count;
                        }
                        if (remainingImage != 0)
                        {
                            SendAscii(source, "ERR incomplete");
                            return;
                        }
                        png = buffer.ToArray();
                    }

                    string caption = string.Empty;
                    if (captionLength > 0)
                    {
                        using (MemoryStream buffer = new MemoryStream())
                        {
                            long remainingText = captionLength;
                            byte[] chunk = new byte[65536];
                            while (remainingText > 0)
                            {
                                int count = source.Read(chunk, 0, (int)Math.Min(chunk.Length, remainingText));
                                if (count <= 0)
                                    break;
                                buffer.Write(chunk, 0, count);
                                remainingText -= count;
                            }
                            if (remainingText != 0)
                            {
                                SendAscii(source, "ERR incomplete");
                                return;
                            }
                            caption = Encoding.UTF8.GetString(buffer.ToArray());
                        }
                    }

                    try
                    {
                        string payloadText = caption;
                        hotkeyForm.BeginInvoke(new Action(delegate
                        {
                            ApplyRemoteClipboardImage(png, payloadText);
                        }));
                    }
                    catch { }
                    SendAscii(source, "OK clipboard");
                    return;
                }

                string directory = ResolveRecvDir();
                // 目标目录（如 下载\typebridge）可能不存在，写 .part 前先建好，
                // 否则 FileStream 抛 DirectoryNotFoundException，发送端只会收到 ERR
                try { System.IO.Directory.CreateDirectory(directory); }
                catch (Exception ex)
                {
                    SendAscii(source, "ERR " + ex.Message);
                    return;
                }

                // 第三段是 "文件名 [续传偏移]"。文件名本身可能以「空格 + 数字」
                // 结尾（如 "IMG 2"），所以只有确实存在匹配的断点记录才把它当偏移。
                string raw = parts[2];
                string name = SafeFileName(raw);
                long offset = 0;
                int space = raw.LastIndexOf(' ');
                if (space > 0)
                {
                    string tail = raw.Substring(space + 1);
                    long candidate;
                    if (long.TryParse(tail, out candidate) && candidate > 0)
                    {
                        string candidateName = SafeFileName(raw.Substring(0, space));
                        string candidateDest = System.IO.Path.Combine(directory, candidateName);
                        if (ReadResumeOffset(candidateDest, candidateName, size) == candidate)
                        {
                            name = candidateName;
                            offset = candidate;
                        }
                    }
                }

                string dest = System.IO.Path.Combine(directory, name);
                string temp = dest + ".part";

                if (resumeQuery)
                {
                    // "RESUME <总大小> <文件名>" 第三段整体是文件名，不带偏移；
                    // 走上面按空格拆偏移的逻辑会把 "IMG 2" 误截成 "IMG"。
                    string queryName = SafeFileName(raw);
                    string queryDest = System.IO.Path.Combine(directory, queryName);
                    SendAscii(source, "HAVE " + ReadResumeOffset(queryDest, queryName, size));
                    return;
                }

                // 同名文件并发上传会共写同一个 .part，按目标路径串行化
                object gate;
                lock (uploadGates)
                {
                    if (!uploadGates.TryGetValue(dest, out gate))
                    {
                        gate = new object();
                        uploadGates[dest] = gate;
                    }
                }

                lock (gate)
                {
                    if (offset > 0 && ReadResumeOffset(dest, name, size) != offset)
                        offset = 0;

                    long remaining = size - offset;
                    using (FileStream output = new FileStream(temp,
                        offset > 0 ? FileMode.Open : FileMode.Create,
                        FileAccess.Write, FileShare.None))
                    {
                        if (offset > 0)
                        {
                            output.Seek(offset, SeekOrigin.Begin);
                            output.SetLength(offset);
                        }
                        else
                        {
                            WriteResumeMeta(dest, name, size);
                        }

                        byte[] buffer = new byte[65536];
                        while (remaining > 0)
                        {
                            int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (count <= 0)
                                break;
                            output.Write(buffer, 0, count);
                            remaining -= count;
                        }
                    }

                    if (remaining != 0)
                    {
                        // 保留 .part 与 .part.info，下次重发同一个文件接着传
                        SendAscii(source, "ERR incomplete");
                        return;
                    }

                    // 原子替换：不会像 Delete+Move 那样在异常时把旧文件和新数据一起丢掉
                    try
                    {
                        if (File.Exists(dest))
                            File.Replace(temp, dest, null);
                        else
                            File.Move(temp, dest);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        if (File.Exists(dest))
                            File.Delete(dest);
                        File.Move(temp, dest);
                    }
                    try { File.Delete(PartMetaPath(dest)); }
                    catch { }

                    SendAscii(source, "OK " + dest);
                }
                string saved = name;
                try
                {
                    hotkeyForm.BeginInvoke(new Action(delegate
                    {
                        ShowTrayTip("收到文件", saved + " → " + directory);
                    }));
                }
                catch { }
            }
            catch (Exception ex)
            {
                try { SendAscii(incoming.GetStream(), "ERR " + ex.Message); }
                catch { }
            }
            finally
            {
                try { incoming.Close(); }
                catch { }
            }
        }

        private string ResolveRecvDir()
        {
            string configured = (recvDir ?? string.Empty).Trim();
            if (configured.Length > 0)
                return Environment.ExpandEnvironmentVariables(configured);
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "typebridge");
        }

        private const long MaxClipImageBytes = 64L * 1024 * 1024;
        private const long MaxClipCaptionBytes = 1024L * 1024;

        private static string PartMetaPath(string dest)
        {
            return dest + ".part.info";
        }

        private void WriteResumeMeta(string dest, string name, long size)
        {
            try
            {
                Dictionary<string, object> meta = new Dictionary<string, object>();
                meta["name"] = name;
                meta["size"] = size;
                System.IO.File.WriteAllText(PartMetaPath(dest), json.Serialize(meta),
                                           new UTF8Encoding(false));
            }
            catch { }
        }

        private long ReadResumeOffset(string dest, string name, long size)
        {
            try
            {
                string part = dest + ".part";
                if (!System.IO.File.Exists(part) || !System.IO.File.Exists(PartMetaPath(dest)))
                    return 0;
                Dictionary<string, object> meta = json.Deserialize<Dictionary<string, object>>(
                    System.IO.File.ReadAllText(PartMetaPath(dest)));
                if (meta == null || !meta.ContainsKey("name") || !meta.ContainsKey("size"))
                    return 0;
                if ((meta["name"] as string) != name || Convert.ToInt64(meta["size"]) != size)
                    return 0;
                long have = new System.IO.FileInfo(part).Length;
                if (have <= 0 || have >= size)
                    return 0;
                return have;
            }
            catch { return 0; }
        }

        private static string SafeFileName(string name)
        {
            string baseName = System.IO.Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
            foreach (char bad in System.IO.Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(bad, '_');
            // GetInvalidFileNameChars 不含 Windows 保留设备名，也不管尾部空格/点；
            // "CON.part" 会直接落到设备 CON 上，File.Move 抛异常并留下 .part。
            baseName = baseName.TrimEnd(' ', '.');
            string stem = System.IO.Path.GetFileNameWithoutExtension(baseName).ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
                || (stem.Length == 4 && stem.StartsWith("COM") && stem[3] >= '1' && stem[3] <= '9')
                || (stem.Length == 4 && stem.StartsWith("LPT") && stem[3] >= '1' && stem[3] <= '9'))
                baseName = "_" + baseName;
            if (baseName.Length == 0 || baseName == "." || baseName == "..")
                baseName = "file";
            return baseName;
        }

        private static string ReadAsciiLine(NetworkStream source)
        {
            List<byte> bytes = new List<byte>();
            while (bytes.Count < 4096)
            {
                int value = source.ReadByte();
                if (value < 0 || value == '\n')
                    break;
                if (value != '\r')
                    bytes.Add((byte)value);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static void SendAscii(NetworkStream target, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text + "\n");
            target.Write(data, 0, data.Length);
            target.Flush();
        }

        private void SendFilesDialog()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要发送到 Ubuntu 的文件";
                dialog.Multiselect = true;
                dialog.CheckFileExists = true;
                dialog.RestoreDirectory = true;
                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                string[] files = dialog.FileNames;
                if (files.Length == 0)
                    return;

                // 上传放到后台线程（UploadFiles 内部起线程），否则大文件会把 UI 和任务栏冻住
                UploadFiles(files);
            }
        }

        /// <summary>UI 线程入口：先建好进度窗口，再把真正的传输丢到后台。</summary>
        private void UploadFiles(string[] files)
        {
            TransferProgressForm progress = ProgressForm();
            progress.BeginTransfer();
            System.Threading.Thread worker = new System.Threading.Thread(
                delegate () { UploadWorker(files, progress); });
            worker.IsBackground = true;
            worker.Start();
        }

        private void UploadWorker(string[] files, TransferProgressForm progress)
        {
            int ok = 0;
            string firstProblem = null;
            string lastDestination = null;

            foreach (string file in files)
            {
                if (progress.CancelRequested)
                {
                    if (firstProblem == null)
                        firstProblem = "已取消";
                    break;
                }

                string error;
                string destination;
                if (UploadOne(file, progress, out error, out destination))
                {
                    ok++;
                    if (destination != null)
                        lastDestination = destination;
                }
                else if (firstProblem == null)
                {
                    firstProblem = System.IO.Path.GetFileName(file) + "：" + (error ?? "发送失败");
                }
            }

            // 落地路径必须回显：之前只说"已发送"，用户根本不知道文件进了 Ubuntu 的哪个目录
            string message = "已发送 " + ok + " / " + files.Length + " 个文件到 Ubuntu";
            if (ok > 0)
            {
                if (files.Length == 1 && lastDestination != null)
                    message += " → " + lastDestination;
                else if (lastDestination != null)
                    message += " → " + System.IO.Path.GetDirectoryName(lastDestination);
            }
            if (firstProblem != null)
                message += "（" + firstProblem + "）";

            progress.Finish(message, false);

            try
            {
                hotkeyForm.BeginInvoke(new Action(delegate
                {
                    UpdateStatus();
                    ShowTrayTip("发送文件", message);
                }));
            }
            catch { }
        }

        private TransferProgressForm progressForm;

        private TransferProgressForm ProgressForm()
        {
            if (progressForm == null || progressForm.IsDisposed)
                progressForm = new TransferProgressForm();
            return progressForm;
        }

        private bool UploadOne(string path, TransferProgressForm progress,
                               out string error, out string destination)
        {
            // ip 是 ini 原始串（可能含逗号），直接交给 ConnectAsync 必然失败。
            // 优先走控制连接实测最快的地址，其余候选依次兜底。
            string lastProblem = null;
            foreach (string host in FileTargetHosts())
            {
                if (FileSender.Upload(host, filePort, path, progress, out error, out destination))
                    return true;
                lastProblem = error;
            }
            error = lastProblem ?? "连不上文件端口 " + filePort;
            destination = null;
            return false;
        }

        /// <summary>文件通道该连哪个地址：已选路的控制连接优先，其余候选兜底。</summary>
        private string[] FileTargetHosts()
        {
            List<string> list = new List<string>();
            if (!string.IsNullOrEmpty(activeHost))
                list.Add(activeHost);
            string[] all = hosts != null && hosts.Length > 0
                ? hosts : Program.ParseHostList(ip);
            foreach (string host in all)
                if (!list.Contains(host))
                    list.Add(host);
            return list.ToArray();
        }

        private static string ReadReply(TcpClient socket)
        {
            return FileSender.ReadReply(socket);
        }

        /// <summary>把 ip 拆成候选地址列表；与 CLI 共用同一套分隔符。</summary>
        private void RefreshHosts()
        {
            hosts = Program.ParseHostList(ip);
        }

        /// <summary>在已经连上的 socket 上打一次 PING，返回往返毫秒；失败返回 -1。</summary>
        private static double MeasurePing(TcpClient client, int timeoutMs)
        {
            try
            {
                client.ReceiveTimeout = timeoutMs;
                NetworkStream socket = client.GetStream();
                byte[] question = Encoding.UTF8.GetBytes("PING\n");
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                socket.Write(question, 0, question.Length);
                socket.Flush();

                byte[] buffer = new byte[256];
                StringBuilder seen = new StringBuilder();
                // Environment.TickCount 是 int，加 timeoutMs 会溢出，且约 24.9 天回绕一次；
                // 用单调的 Stopwatch 计时就没有这个窗口。
                while ((System.Diagnostics.Stopwatch.GetTimestamp() - started)
                       * 1000.0 / System.Diagnostics.Stopwatch.Frequency < timeoutMs)
                {
                    int count = socket.Read(buffer, 0, buffer.Length);
                    if (count <= 0)
                        break;
                    seen.Append(Encoding.UTF8.GetString(buffer, 0, count));
                    if (seen.ToString().IndexOf("PONG", StringComparison.Ordinal) >= 0)
                    {
                        return (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    }
                }
                return -1;
            }
            catch
            {
                return -1;
            }
            finally
            {
                // 测完必须把接收超时清掉：这条连接会被直接拿去当正式会话，
                // 留着 700ms 超时的话，空闲超过 700ms 读取就抛异常，读线程直接退出，
                // 于是 PONG 再也收不到 —— 表现就是"鼠标在 Ubuntu 上但 3 秒后被强行收回"。
                try { client.ReceiveTimeout = 0; }
                catch { }
            }
        }

        private void ConnectToServer()
        {
            if (stream != null || connecting)
                return;
            connecting = true;
            int targetPort = port;
            string[] targets = hosts.Length > 0 ? hosts : new string[] { ip };
            string signature = string.Join(",", targets);

            // 连接放到线程池：原来 Wait(400) 每秒阻塞 UI 线程 400ms，
            // 期间全局钩子回调超时，按键鼠标会漏进 Windows。
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<TcpClient> opened = new List<TcpClient>();
                StringBuilder report = new StringBuilder();
                double bestRtt = double.MaxValue;
                TcpClient best = null;
                string bestHost = null;

                // 并行探测：TUN / 代理可能把某条路径拖到几百毫秒，另一条却是直连，
                // 所以每次都实测，用最快的那个，而不是死守配置里的第一个地址。
                // 先把所有候选的连接同时发出去，避免一条不通的地址拖慢整次连接
                List<System.Threading.Tasks.Task> pending = new List<System.Threading.Tasks.Task>();
                List<TcpClient> probes = new List<TcpClient>();
                foreach (string target in targets)
                {
                    TcpClient probe = new TcpClient();
                    try { probe.NoDelay = true; } catch { }
                    probes.Add(probe);
                    try { pending.Add(probe.ConnectAsync(target, targetPort)); }
                    catch { }
                }
                try
                {
                    if (pending.Count > 0)
                        System.Threading.Tasks.Task.WaitAll(pending.ToArray(), 900);
                }
                catch { }

                for (int index = 0; index < probes.Count; index++)
                {
                    string target = targets[index];
                    TcpClient probe = probes[index];
                    if (!probe.Connected)
                    {
                        try { probe.Close(); }
                        catch { }
                        report.Append(target + " 连不上；");
                        continue;
                    }

                    double rtt = MeasurePing(probe, 700);
                    if (rtt <= 0)
                    {
                        try { probe.Close(); }
                        catch { }
                        report.Append(target + " 无响应；");
                        continue;
                    }
                    report.Append(target + " " + rtt.ToString("0.0") + "ms；");
                    opened.Add(probe);
                    if (rtt < bestRtt)
                    {
                        bestRtt = rtt;
                        best = probe;
                        bestHost = target;
                    }
                }

                // 没选中的按顺序关掉，只留最快那条
                for (int i = 0; i < opened.Count; i++)
                {
                    if (!object.ReferenceEquals(opened[i], best))
                    {
                        try { opened[i].Close(); }
                        catch { }
                    }
                }

                TcpClient candidate = best;
                string host = bestHost;
                bool ok = candidate != null;
                if (ok)
                    Log.Write("path pick: " + host + ":" + targetPort + " rtt="
                        + bestRtt.ToString("0.0") + "ms  候选[" + report + "]");
                else
                    Log.Write("connect failed: " + report);

                try
                {
                    string captured = signature;
                    hotkeyForm.BeginInvoke(new MethodInvoker(delegate
                    {
                        connecting = false;
                        string currentSignature = string.Join(",",
                            hosts.Length > 0 ? hosts : new string[] { ip });
                        if (!ok || stream != null || currentSignature != captured || targetPort != port)
                        {
                            if (ok)
                            {
                                try { candidate.Close(); }
                                catch { }
                            }
                            return;
                        }
                        client = candidate;
                        stream = candidate.GetStream();
                        activeHost = host;
                        lastRttMs = bestRtt;
                        // 刚连上不能立刻被看门狗判死
                        lastPongUtc = DateTime.UtcNow;
                        Log.Write("connected " + host + ":" + targetPort
                            + " (" + bestRtt.ToString("0.0") + "ms)");
                        // 写超时：半死链路上 Write 最多卡 4 秒就报错，
                        // 不至于把 UI 线程外的发送线程卡几十秒才判死
                        try { stream.WriteTimeout = 4000; }
                        catch { }
                        EnableKeepAlive(candidate);
                        StartSender();
                        StartReader(stream);
                        // 边缘切换开启且鼠标还在本机时，让鼠标钩子开始盯屏幕边缘
                        mouseHook.Watching = edgeSwitch && !mouseMode;
                        UpdateStatus();

                        // 设置窗口保存会先断开再异步重连；等 stream 真正就绪才恢复，
                        // 否则 Set*Mode 的 stream==null 守卫会把恢复挡掉。
                        if (resumeKeyboardAfterReconnect || resumeMouseAfterReconnect)
                        {
                            bool wantKeyboard = resumeKeyboardAfterReconnect;
                            bool wantMouse = resumeMouseAfterReconnect;
                            resumeKeyboardAfterReconnect = false;
                            resumeMouseAfterReconnect = false;
                            SetKeyboardMode(wantKeyboard, false);
                            if (wantMouse)
                                SetMouseMode(true, false);
                        }
                    }));
                }
                catch
                {
                    // 句柄销毁导致 BeginInvoke 抛异常时，connecting 会永远卡在 true，
                    // 之后每秒的重连都被守卫挡住。这里复位并关掉已探测好的候选连接。
                    connecting = false;
                    if (candidate != null)
                    {
                        try { candidate.Close(); }
                        catch { }
                    }
                }
            });
        }

        /// <summary>5 秒空闲后每 2 秒发 TCP 探测包，死链路几秒内触发写失败，
        /// 而不是等系统默认的探测周期（可达数分钟）。</summary>
        private static void EnableKeepAlive(TcpClient tcp)
        {
            try
            {
                // SIO_KEEPALIVE_VALS 是 3 个 32 位 u_long（onoff / time / interval），
                // 共 12 字节。原来用 ulong(8B) 写到 offset 8 会越界抛异常被 catch 吞掉，
                // 结果 keepalive 从未生效，死链路只能靠写超时发现。
                byte[] values = new byte[12];
                BitConverter.GetBytes((uint)1).CopyTo(values, 0);
                BitConverter.GetBytes((uint)5000).CopyTo(values, 4);
                BitConverter.GetBytes((uint)2000).CopyTo(values, 8);
                tcp.Client.IOControl(IOControlCode.KeepAliveValues, values, null);
            }
            catch { }
        }

        private void StartSender()
        {
            NetworkStream target = stream;
            Queue<string> queue;
            int generation;
            lock (sendGate)
            {
                queue = new Queue<string>();
                pendingLines = queue;
                generation = ++senderGeneration;
                senderRunning = true;
            }
            senderThread = new System.Threading.Thread(
                delegate() { SenderLoop(target, queue, generation); });
            senderThread.IsBackground = true;
            senderThread.Name = "TypeBridge-Sender";
            senderThread.Start();
        }

        private void SenderLoop(NetworkStream target, Queue<string> queue, int generation)
        {
            while (senderRunning && senderGeneration == generation)
            {
                string line = null;
                lock (sendGate)
                {
                    if (queue.Count > 0)
                        line = queue.Dequeue();
                }
                if (line == null)
                {
                    System.Threading.Thread.Sleep(2);
                    continue;
                }
                if (target == null || senderGeneration != generation)
                    continue;
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                    target.Write(bytes, 0, bytes.Length);
                    target.Flush();
                }
                catch
                {
                    NotifyConnectionLost(generation);
                    return;
                }
            }
        }

        /// <summary>发送线程发现断链后回到 UI 线程收尾：断开并把键鼠切回 Windows。
        /// 这是兜底保护——即使切换热键在断链期间失灵，用户也不会被卡在 Ubuntu 模式。</summary>
        private void NotifyConnectionLost(int generation)
        {
            // 旧世代的发送线程写失败时不能动刚建好的新连接
            if (generation != senderGeneration)
                return;
            try
            {
                hotkeyForm.BeginInvoke(new MethodInvoker(delegate
                {
                    bool wasActive = keyboardMode || mouseMode;
                    Log.Write("connection lost (wasActive=" + wasActive + ")");
                    CloseConnection();
                    if (mouseMode)
                        SetMouseMode(false, false);
                    if (keyboardMode)
                        SetKeyboardMode(false, false);
                    mouseMode = false;
                    UpdateStatus();
                    if (wasActive)
                        ShowTrayTip("连接已断开", "键盘鼠标已切回 Windows，自动重连中");
                }));
            }
            catch { }
        }

        /// <summary>
        /// 输入自愈：低级键盘/鼠标钩子有概率被 Windows 静默摘除（回调偶发超时被摘，
        /// 进程不收到任何通知，句柄值却仍非 0）。一旦发生，按热键和推边缘都石沉大海，
        /// 而进程、TCP 连接、网络看门狗全部正常 —— 只能靠这里定期强制重装恢复。
        ///
        /// 只在非接管态、非设置窗口时做：接管态正在转发输入，重装会漏事件；
        /// 设置窗口本来就主动摘了热键和钩子，不去打扰。重注册热键可顺带恢复被
        /// 其他程序挤掉后一直没回来的全局热键。
        /// </summary>
        private void SelfHealInput()
        {
            if (keyboardMode || mouseMode || settingsOpen)
                return;
            if ((DateTime.UtcNow - lastHookHealUtc).TotalSeconds < 30)
                return;
            lastHookHealUtc = DateTime.UtcNow;
            try
            {
                hook.Reinstall();
                mouseHook.Reinstall();
                ApplyHotkeys();
                Log.Write("input self-heal: hooks + hotkeys reinstalled");
            }
            catch (Exception ex)
            {
                Log.Write("input self-heal failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 每秒一次：发心跳；半开连接收不到 PONG 就把键鼠还给本机，
        /// 避免"网络断了但 TCP 没报错"时人被困在 Ubuntu 模式。
        /// </summary>
        private void ConnectionTick()
        {
            SelfHealInput();
            if (stream != null)
            {
                pingSentTs = System.Diagnostics.Stopwatch.GetTimestamp();
                SendLine("PING");

                // 接管态 3 秒兜底（不能把人困在远端）；Windows 模式下放宽到 8 秒。
                // 3.6.2 曾让两种状态都用 3 秒：一次 4 秒的网络抖动就触发断线，
                // 而链路恢复前重连要十几秒，期间 Watching=false、边缘切屏完全没反应，
                // 表现就是「推两下没成功，隔一阵才切过去」。
                // 链路真死时 SenderLoop 的 WriteTimeout(4s) 会走 NotifyConnectionLost 兜底。
                bool activeMode = keyboardMode || mouseMode || remoteDrive;
                double limitSeconds = activeMode ? 3 : 8;
                double silentSeconds = (DateTime.UtcNow - lastPongUtc).TotalSeconds;
                if (silentSeconds > limitSeconds)
                {
                    Log.Write("watchdog: no pong for " + silentSeconds.ToString("0.0")
                        + "s (limit=" + limitSeconds.ToString("0") + "), reconnect (wasActive="
                        + activeMode + ")");
                    if (activeMode)
                    {
                        if (remoteDrive)
                        {
                            ShowTrayTip("已收回控制", "和 Ubuntu 的连接断了，键盘鼠标回到本机");
                            EndRemoteDrive("连接超时", false);
                        }
                        else
                        {
                            ShowTrayTip("已切回本机", "没收到 Ubuntu 回应，先把键盘鼠标还给你");
                        }
                        SetKeyboardMode(false, false);
                        SetMouseMode(false, false);
                    }
                    CloseConnection();
                    return;
                }
            }

            ConnectToServer();
        }

        private void CloseConnection()
        {
            // 反向接管中把键放掉：否则 Ubuntu 一断网，本机就卡在"Ctrl 一直按着"
            remoteDrive = false;
            RemoteInput.ReleaseAll();
            // 先补发鼠标 up，再断开，避免 Ubuntu 端卡在拖拽状态
            mouseHook.ReleaseAll();
            StopReader();
            mouseHook.Watching = false;
            try
            {
                if (client != null)
                    client.Close();
            }
            catch { }
            client = null;
            stream = null;
            // 作废旧发送线程：它持有自己的队列与流，醒来后看到世代不符就退出
            senderGeneration++;
            senderRunning = false;
            lock (sendGate)
            {
                pendingLines.Clear();
            }
            mouseHook.Connected = false;
            mouseHook.RestoreCursor();
            // 断了还留在 Ubuntu 模式 = 把用户锁在远端：立刻还给本机
            if (mouseMode)
            {
                mouseMode = false;
                mouseHook.Enabled = false;
                UpdateStatus();
            }
        }

        /// <summary>只把指令排队，立即返回。真正的网络写在后台发送线程里。</summary>
        private bool SendLine(string line)
        {
            lock (sendGate)
            {
                if (stream == null)
                    return false;
                pendingLines.Enqueue(line);
                lastEnqueueUtc = DateTime.UtcNow;
                return true;
            }
        }

        private bool SendText(string text)
        {
            if (text.Length == 0)
                return true;
            return SendLine("T " + json.Serialize(text));
        }

        protected override void ExitThreadCore()
        {
            if (!disposed)
            {
                disposed = true;
                senderRunning = false;
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
