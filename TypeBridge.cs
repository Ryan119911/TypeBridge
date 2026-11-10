using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("TypeBridge")]
[assembly: AssemblyDescription("Windowless Windows to Ubuntu keyboard bridge")]
[assembly: AssemblyProduct("TypeBridge")]
[assembly: AssemblyVersion("3.0.0.0")]

namespace TypeBridgeApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(
                true, "Local\\TypeBridge-SingleInstance-7C4A", out created))
            {
                if (!created)
                    return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (BridgeApplicationContext context = new BridgeApplicationContext())
                {
                    Application.Run(context);
                }
            }
        }
    }

    internal sealed class HotkeyForm : Form
    {
        private const int WmHotKey = 0x0312;
        private const int HotKeyId = 0x5472;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        internal bool HotKeyAvailable { get; private set; }
        internal event EventHandler HotKeyPressed;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HotKeyAvailable = RegisterHotKey(Handle, HotKeyId, ModControl | ModAlt, (uint)Keys.U);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (HotKeyAvailable)
            {
                UnregisterHotKey(Handle, HotKeyId);
                HotKeyAvailable = false;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotKey && m.WParam.ToInt32() == HotKeyId)
            {
                EventHandler handler = HotKeyPressed;
                if (handler != null)
                    handler(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
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
            ClientSize = new Size(380, 84);
            BackColor = Color.FromArgb(30, 30, 30);
            Opacity = 0.96;

            titleLabel.AutoSize = false;
            titleLabel.SetBounds(16, 10, 348, 20);
            titleLabel.ForeColor = Color.Silver;
            titleLabel.Font = new Font("Microsoft YaHei", 9F);
            titleLabel.Text = "TypeBridge";

            messageLabel.AutoSize = false;
            messageLabel.SetBounds(16, 32, 348, 38);
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
                workArea.Right - Width - 24,
                workArea.Bottom - Height - 24);

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
        private IntPtr hook = IntPtr.Zero;

        internal bool Enabled { get; set; }
        internal event EventHandler<BridgeKeyEventArgs> Key;

        internal GlobalKeyboardHook()
        {
            callback = HookCallback;
        }

        internal void Install()
        {
            if (hook != IntPtr.Zero)
                return;
            hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static List<string> ActiveModifiers()
        {
            List<string> modifiers = new List<string>();
            if (((GetAsyncKeyState(0xA2) & 0x8000) != 0) ||
                ((GetAsyncKeyState(0xA3) & 0x8000) != 0))
                modifiers.Add("ctrl");
            if (((GetAsyncKeyState(0xA4) & 0x8000) != 0) ||
                ((GetAsyncKeyState(0xA5) & 0x8000) != 0))
                modifiers.Add("alt");
            if ((GetAsyncKeyState(0x10) & 0x8000) != 0)
                modifiers.Add("shift");
            if (((GetAsyncKeyState(0x5B) & 0x8000) != 0) ||
                ((GetAsyncKeyState(0x5C) & 0x8000) != 0))
                modifiers.Add("super");
            return modifiers;
        }

        private static bool IsToggleHotKey(int virtualKey, List<string> modifiers)
        {
            return virtualKey == (int)Keys.U &&
                modifiers.Count == 2 &&
                modifiers.Contains("ctrl") &&
                modifiers.Contains("alt");
        }

        private static string SpecialKeyName(int virtualKey)
        {
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

        private static string KeyText(KbdData data)
        {
            byte[] state = new byte[256];
            if ((GetAsyncKeyState(0x10) & 0x8000) != 0)
                state[0x10] = 0x80;
            if ((GetKeyState(0x14) & 1) != 0)
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
                        if (suppressedKeys.Remove(vk))
                            return (IntPtr)1;
                        return CallNextHookEx(hook, nCode, wParam, lParam);
                    }

                    if (Enabled)
                    {
                        List<string> modifiers = ActiveModifiers();
                        if (!IsToggleHotKey(vk, modifiers))
                        {
                            string baseKey = BaseKeyName(vk);
                            string text = null;
                            string keyName = null;

                            if (modifiers.Count > 0 && baseKey != null)
                            {
                                keyName = string.Join("+", modifiers.ToArray()) + "+" + baseKey;
                            }
                            else if (modifiers.Count == 0)
                            {
                                keyName = SpecialKeyName(vk);
                                if (keyName == null)
                                    text = KeyText(data);
                            }

                            if (text != null || keyName != null)
                            {
                                suppressedKeys.Add(vk);
                                EventHandler<BridgeKeyEventArgs> handler = Key;
                                if (handler != null)
                                {
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

    internal sealed class SettingsForm : Form
    {
        private readonly TextBox hostBox = new TextBox();
        private readonly TextBox portBox = new TextBox();
        private readonly Label errorLabel = new Label();

        internal string Host { get; private set; }
        internal int Port { get; private set; }

        internal SettingsForm(string host, int port, bool firstRun)
        {
            Host = host;
            Port = port;

            Text = firstRun ? "TypeBridge 首次设置" : "TypeBridge 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(420, 220);

            Label hostLabel = new Label();
            hostLabel.Text = "Ubuntu 地址（Tailscale IP、局域网 IP 或主机名）";
            hostLabel.AutoSize = true;
            hostLabel.Location = new Point(16, 16);

            hostBox.Text = host;
            hostBox.SetBounds(16, 42, 388, 26);
            hostBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Label portLabel = new Label();
            portLabel.Text = "端口";
            portLabel.AutoSize = true;
            portLabel.Location = new Point(16, 80);

            portBox.Text = port.ToString();
            portBox.SetBounds(16, 104, 100, 26);

            Label hint = new Label();
            hint.Text = "默认 4321；请填写你自己的 Ubuntu 地址。";
            hint.AutoSize = true;
            hint.ForeColor = Color.Gray;
            hint.Location = new Point(130, 108);

            errorLabel.AutoSize = false;
            errorLabel.SetBounds(16, 138, 388, 34);
            errorLabel.ForeColor = Color.Firebrick;

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.SetBounds(230, 180, 80, 30);

            Button saveButton = new Button();
            saveButton.Text = "保存";
            saveButton.SetBounds(320, 180, 84, 30);
            saveButton.Click += delegate
            {
                string newHost = hostBox.Text.Trim();
                if (newHost.Length == 0)
                {
                    errorLabel.Text = "请输入 Ubuntu 地址。";
                    return;
                }

                int newPort;
                if (!int.TryParse(portBox.Text.Trim(), out newPort) || newPort < 1 || newPort > 65535)
                {
                    errorLabel.Text = "端口必须是 1 到 65535 之间的数字。";
                    return;
                }

                Host = newHost;
                Port = newPort;
                DialogResult = DialogResult.OK;
                Close();
            };

            AcceptButton = saveButton;
            CancelButton = cancelButton;

            Controls.Add(hostLabel);
            Controls.Add(hostBox);
            Controls.Add(portLabel);
            Controls.Add(portBox);
            Controls.Add(hint);
            Controls.Add(errorLabel);
            Controls.Add(cancelButton);
            Controls.Add(saveButton);
        }
    }

    internal sealed class BridgeApplicationContext : ApplicationContext
    {
        private readonly HotkeyForm hotkeyForm = new HotkeyForm();
        private readonly GlobalKeyboardHook hook = new GlobalKeyboardHook();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ToastForm toast = new ToastForm();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem toggleItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem settingsItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem exitItem = new ToolStripMenuItem();
        private readonly Timer connectionTimer = new Timer();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        private TcpClient client;
        private NetworkStream stream;
        private string ip = "127.0.0.1";
        private int port = 4321;
        private bool ubuntuMode;
        private bool configLoaded;
        private bool settingsOpen;
        private bool disposed;

        internal BridgeApplicationContext()
        {
            LoadConfiguration();

            hotkeyForm.ShowInTaskbar = false;
            hotkeyForm.Visible = false;
            IntPtr unusedHandle = hotkeyForm.Handle;
            GC.KeepAlive(unusedHandle);
            hotkeyForm.HotKeyPressed += delegate { ToggleMode(); };

            hook.Key += HookKey;
            hook.Install();
            hook.Enabled = false;

            toggleItem.Text = "切换到 Ubuntu\tCtrl+Alt+U";
            settingsItem.Text = "设置 Ubuntu 地址...";
            exitItem.Text = "退出 TypeBridge";
            exitItem.Click += delegate { ExitThread(); };
            toggleItem.Click += delegate { ToggleMode(); };
            settingsItem.Click += delegate { ShowSettings(false); };
            trayMenu.Items.Add(toggleItem);
            trayMenu.Items.Add(settingsItem);
            trayMenu.Items.Add(exitItem);

            tray.ContextMenuStrip = trayMenu;
            tray.Icon = SystemIcons.Application;
            tray.DoubleClick += delegate { ToggleMode(); };
            tray.Visible = true;

            connectionTimer.Interval = 1000;
            connectionTimer.Tick += delegate { ConnectToServer(); };
            connectionTimer.Start();

            SetMode(false, false);
            ShowTrayTip("TypeBridge 已启动", "Ctrl + Alt + U 切换到 Ubuntu 输入");
            if (!hotkeyForm.HotKeyAvailable)
                ShowTrayTip("快捷键不可用", "Ctrl+Alt+U 已被其他程序占用，请用托盘菜单切换");
            if (!configLoaded)
            {
                hotkeyForm.BeginInvoke(new Action(delegate { ShowSettings(true); }));
            }
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
                        ip = value;
                    if (key == "port")
                    {
                        int configuredPort;
                        if (int.TryParse(value, out configuredPort) && configuredPort > 0 && configuredPort <= 65535)
                            port = configuredPort;
                    }
                }
            }
            catch
            {
                // 配置不可读时继续使用默认 Tailscale 地址。
            }
        }

        private void ShowSettings(bool firstRun)
        {
            if (settingsOpen)
                return;
            settingsOpen = true;
            try
            {
                using (SettingsForm form = new SettingsForm(ip, port, firstRun))
                {
                    if (form.ShowDialog() != DialogResult.OK)
                        return;

                    ip = form.Host;
                    port = form.Port;
                    if (!SaveConfiguration())
                        return;

                    configLoaded = true;
                    CloseConnection();
                    UpdateStatus();
                    ShowTrayTip("设置已保存", "正在连接 " + ip + ":" + port);
                    ConnectToServer();
                }
            }
            finally
            {
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
                    new string[] { "ip=" + ip, "port=" + port.ToString() },
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

        private void ToggleMode()
        {
            SetMode(!ubuntuMode, true);
        }

        private void SetMode(bool remote, bool notify)
        {
            ubuntuMode = remote;
            hook.Enabled = remote;
            UpdateStatus();
            if (notify)
                ShowTrayTip("TypeBridge", "当前输入：" + (remote ? "Ubuntu（使用 Ubuntu 输入法）" : "Windows"));
        }

        private void UpdateStatus()
        {
            if (ubuntuMode)
            {
                tray.Text = stream != null ? "TypeBridge → Ubuntu（已连接）" : "TypeBridge → Ubuntu（未连接）";
                tray.Icon = SystemIcons.Information;
                toggleItem.Text = "切换到 Windows\tCtrl+Alt+U";
            }
            else
            {
                tray.Text = "TypeBridge → Windows";
                tray.Icon = SystemIcons.Application;
                toggleItem.Text = "切换到 Ubuntu\tCtrl+Alt+U";
            }
        }

        private void ShowTrayTip(string title, string text)
        {
            toast.ShowMessage(title, text);
        }

        private void HookKey(object sender, BridgeKeyEventArgs e)
        {
            if (!ubuntuMode)
                return;
            if (e.Text != null)
                SendText(e.Text);
            else if (!string.IsNullOrEmpty(e.KeyName))
                SendLine("K " + e.KeyName);
        }

        private void ConnectToServer()
        {
            if (stream != null)
                return;

            TcpClient candidate = null;
            try
            {
                candidate = new TcpClient();
                if (candidate.ConnectAsync(ip, port).Wait(400))
                {
                    client = candidate;
                    stream = candidate.GetStream();
                }
                else
                {
                    candidate.Close();
                }
            }
            catch
            {
                if (candidate != null)
                {
                    try { candidate.Close(); }
                    catch { }
                }
            }
            UpdateStatus();
        }

        private void CloseConnection()
        {
            try
            {
                if (client != null)
                    client.Close();
            }
            catch { }
            client = null;
            stream = null;
        }

        private bool SendLine(string line)
        {
            if (stream == null)
                return false;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                return true;
            }
            catch
            {
                CloseConnection();
                UpdateStatus();
                return false;
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
                connectionTimer.Stop();
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
