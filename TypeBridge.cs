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
[assembly: AssemblyDescription("Windowless Windows to Ubuntu keyboard and mouse bridge")]
[assembly: AssemblyProduct("TypeBridge")]
[assembly: AssemblyVersion("3.4.0.0")]

namespace TypeBridgeApp
{
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
                default: return Keys.None;
            }
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

    /// <summary>点击后按下组合键即可录制的快捷键输入框。</summary>
    internal sealed class HotkeyBox : TextBox
    {
        private Hotkey value;

        internal Hotkey Value
        {
            get { return value; }
            set
            {
                this.value = value;
                Text = value == null ? string.Empty : value.ToString();
            }
        }

        internal HotkeyBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
            BackColor = SystemColors.Window;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.None)
                return base.ProcessCmdKey(ref msg, keyData);

            if (key == Keys.Back || key == Keys.Delete)
            {
                value = null;
                Text = string.Empty;
                return true;
            }

            if (key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
                key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
                key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu ||
                key == Keys.LWin || key == Keys.RWin)
            {
                return true;
            }

            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool alt = (keyData & Keys.Alt) == Keys.Alt;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;
            // Win 组合键会先被系统截获，无法可靠录制；配置文件里仍可手写 Win+...
            bool win = false;

            if (!ctrl && !alt && !shift &&
                (key == Keys.Tab || key == Keys.Return || key == Keys.Escape))
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }

            value = new Hotkey(ctrl, alt, shift, win, key);
            Text = value.ToString();
            return true;
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
        private IntPtr hook = IntPtr.Zero;
        private bool enabled;
        private bool capsLockState;

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
                    modifierDown.Clear();
                    modifierUsed.Clear();
                    suppressedKeys.Clear();
                }
            }
        }

        internal event EventHandler<BridgeKeyEventArgs> Key;
        internal event EventHandler ToggleRequested;

        /// <summary>Ubuntu 模式下用于切回 Windows 的组合键，与托盘注册的热键保持一致。</summary>
        internal Hotkey ToggleHotkey { get; set; }

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

        /// <summary>修饰键按下状态直接读硬件状态，不依赖事件跟踪，
        /// 这样即使某次修饰键按下事件因钩子超时被漏掉，组合键仍能正确识别。</summary>
        private List<string> ActiveModifiers()
        {
            List<string> modifiers = new List<string>();
            if (IsDown(0xA2) || IsDown(0xA3))
                modifiers.Add("ctrl");
            if (IsDown(0xA4) || IsDown(0xA5))
                modifiers.Add("alt");
            if (IsDown(0xA0) || IsDown(0xA1))
                modifiers.Add("shift");
            if (IsDown(0x5B) || IsDown(0x5C))
                modifiers.Add("super");
            return modifiers;
        }

        /// <summary>修饰键状态失步自愈：物理上已松开、但钩子仍标记为按下的修饰键清掉。
        /// 残留的幽灵修饰键会让之后所有组合键都带上它（表现为"键盘失灵"）。</summary>
        private void ReconcileModifiers()
        {
            int[] tracked = new int[modifierDown.Count];
            modifierDown.CopyTo(tracked);
            foreach (int key in tracked)
            {
                if (!IsDown(key))
                {
                    modifierDown.Remove(key);
                    modifierUsed.Remove(key);
                    suppressedKeys.Remove(key);
                }
            }
        }

        private void MarkModifiersUsed()
        {
            foreach (int key in modifierDown)
                modifierUsed.Add(key);
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
            if ((GetAsyncKeyState(0x10) & 0x8000) != 0)
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
                            if (wasDown)
                                return (IntPtr)1;
                        }
                        if (suppressedKeys.Remove(vk))
                            return (IntPtr)1;
                        return CallNextHookEx(hook, nCode, wParam, lParam);
                    }

                    if (!Enabled)
                        return CallNextHookEx(hook, nCode, wParam, lParam);

                    if (vk == (int)Keys.CapsLock)
                    {
                        capsLockState = !capsLockState;
                        suppressedKeys.Add(vk);
                        return (IntPtr)1;
                    }

                    ReconcileModifiers();

                    if (IsModifierKey(vk))
                    {
                        if (modifierDown.Add(vk))
                            suppressedKeys.Add(vk);
                        return (IntPtr)1;
                    }

                    List<string> modifiers = ActiveModifiers();
                    if (ToggleHotkey != null && ToggleHotkey.Matches(modifiers, vk))
                    {
                        MarkModifiersUsed();
                        suppressedKeys.Add(vk);
                        EventHandler toggle = ToggleRequested;
                        if (toggle != null)
                        {
                            try { toggle(this, EventArgs.Empty); }
                            catch { }
                        }
                        return (IntPtr)1;
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
                        suppressedKeys.Add(vk);
                        RaiseKey(text, keyName);
                        return (IntPtr)1;
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

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        private readonly HookProc callback;
        private readonly Timer flushTimer = new Timer();
        private readonly HashSet<int> pressedButtons = new HashSet<int>();
        private IntPtr hook = IntPtr.Zero;
        private int anchorX;
        private int anchorY;
        private int pendingDx;
        private int pendingDy;
        private int restoreX;
        private int restoreY;
        private bool hasRestore;
        private bool pinned;

        private bool enabled;
        private bool connected;

        internal bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                UpdateTimer();
            }
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
            // 停表前把残留位移发出去，避免最后一小段移动被吞掉
            Flush();
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
            if (hasRestore)
            {
                SetCursorPos(restoreX, restoreY);
                hasRestore = false;
            }
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
                if (!Active)
                {
                    pendingDx = 0;
                    pendingDy = 0;
                    pressedButtons.Clear();
                    return CallNextHookEx(hook, nCode, wParam, lParam);
                }

                MouseData data = (MouseData)Marshal.PtrToStructure(lParam, typeof(MouseData));
                int message = wParam.ToInt32();

                switch (message)
                {
                    case WmMouseMove:
                    {
                        int dx = data.X - anchorX;
                        int dy = data.Y - anchorY;
                        if (dx != 0 || dy != 0)
                        {
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

        private void Flush()
        {
            int dx = pendingDx;
            int dy = pendingDy;
            if (dx == 0 && dy == 0)
                return;
            pendingDx = 0;
            pendingDy = 0;
            if (!Active)
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

    internal sealed class SettingsForm : Form
    {
        private readonly TextBox hostBox = new TextBox();
        private readonly TextBox portBox = new TextBox();
        private readonly HotkeyBox keyboardBox = new HotkeyBox();
        private readonly HotkeyBox mouseBox = new HotkeyBox();
        private readonly CheckBox linkBox = new CheckBox();
        private readonly Label mouseLabel = new Label();
        private readonly Label errorLabel = new Label();

        internal string Host { get; private set; }
        internal int Port { get; private set; }
        internal Hotkey KeyboardHotkey { get; private set; }
        internal Hotkey MouseHotkey { get; private set; }
        internal bool LinkMouse { get; private set; }

        internal SettingsForm(string host, int port, Hotkey keyboardHotkey, Hotkey mouseHotkey,
            bool linkMouse, bool firstRun)
        {
            Host = host;
            Port = port;
            KeyboardHotkey = keyboardHotkey;
            MouseHotkey = mouseHotkey;
            LinkMouse = linkMouse;

            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            Text = firstRun ? "TypeBridge 首次设置" : "TypeBridge 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            // 纯代码创建的窗体不会自动缩放，按系统 DPI 手动换算，避免高缩放下文字截断。
            ClientSize = new Size(UiScale.S(460), UiScale.S(410));

            Label hostLabel = new Label();
            hostLabel.Text = "Ubuntu 地址（Tailscale IP、局域网 IP 或主机名）";
            hostLabel.AutoSize = true;
            hostLabel.Location = new Point(UiScale.S(16), UiScale.S(14));

            hostBox.Text = host;
            hostBox.SetBounds(UiScale.S(16), UiScale.S(40), UiScale.S(428), UiScale.S(26));
            hostBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Label portLabel = new Label();
            portLabel.Text = "端口";
            portLabel.AutoSize = true;
            portLabel.Location = new Point(UiScale.S(16), UiScale.S(78));

            portBox.Text = port.ToString();
            portBox.SetBounds(UiScale.S(16), UiScale.S(102), UiScale.S(90), UiScale.S(26));

            Label hint = new Label();
            hint.Text = "默认 4321；请填写你自己的 Ubuntu 地址。";
            hint.AutoSize = true;
            hint.ForeColor = Color.Gray;
            hint.Location = new Point(UiScale.S(120), UiScale.S(106));

            GroupBox hotkeyGroup = new GroupBox();
            hotkeyGroup.Text = "切换快捷键";
            hotkeyGroup.SetBounds(UiScale.S(16), UiScale.S(140), UiScale.S(428), UiScale.S(158));

            linkBox.Text = "键盘和鼠标一起切换（推荐）";
            linkBox.Checked = linkMouse;
            linkBox.SetBounds(UiScale.S(14), UiScale.S(18), UiScale.S(390), UiScale.S(24));
            linkBox.CheckedChanged += delegate
            {
                mouseBox.Enabled = !linkBox.Checked;
                mouseLabel.Enabled = !linkBox.Checked;
            };

            Label keyboardLabel = new Label();
            keyboardLabel.Text = "键盘快捷键";
            keyboardLabel.AutoSize = true;
            keyboardLabel.Location = new Point(UiScale.S(14), UiScale.S(50));

            keyboardBox.SetBounds(UiScale.S(104), UiScale.S(46), UiScale.S(190), UiScale.S(26));
            keyboardBox.Value = keyboardHotkey;

            mouseLabel.Text = "鼠标快捷键";
            mouseLabel.AutoSize = true;
            mouseLabel.Location = new Point(UiScale.S(14), UiScale.S(84));

            mouseBox.SetBounds(UiScale.S(104), UiScale.S(80), UiScale.S(190), UiScale.S(26));
            mouseBox.Value = mouseHotkey;
            mouseBox.Enabled = !linkMouse;
            mouseLabel.Enabled = !linkMouse;

            Label hint2 = new Label();
            hint2.Text = "点击输入框后按下组合键，至少包含 Ctrl、Alt 或 Shift；取消联动后鼠标快捷键才生效。";
            hint2.AutoSize = false;
            hint2.SetBounds(UiScale.S(14), UiScale.S(114), UiScale.S(400), UiScale.S(38));
            hint2.ForeColor = Color.Gray;

            hotkeyGroup.Controls.Add(linkBox);
            hotkeyGroup.Controls.Add(keyboardLabel);
            hotkeyGroup.Controls.Add(keyboardBox);
            hotkeyGroup.Controls.Add(mouseLabel);
            hotkeyGroup.Controls.Add(mouseBox);
            hotkeyGroup.Controls.Add(hint2);

            errorLabel.AutoSize = false;
            errorLabel.SetBounds(UiScale.S(16), UiScale.S(306), UiScale.S(428), UiScale.S(40));
            errorLabel.ForeColor = Color.Firebrick;

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.SetBounds(UiScale.S(252), UiScale.S(358), UiScale.S(84), UiScale.S(30));

            Button saveButton = new Button();
            saveButton.Text = "保存";
            saveButton.SetBounds(UiScale.S(344), UiScale.S(358), UiScale.S(100), UiScale.S(30));
            saveButton.Click += delegate
            {
                errorLabel.Text = string.Empty;

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

                Hotkey newKeyboard = keyboardBox.Value;
                if (newKeyboard == null || !newKeyboard.HasModifier)
                {
                    errorLabel.Text = "键盘快捷键需要至少一个修饰键（Ctrl / Alt / Shift）。";
                    return;
                }

                Hotkey newMouse = mouseBox.Value;
                if (newMouse == null)
                    newMouse = Hotkey.DefaultMouse();
                if (!newMouse.HasModifier)
                {
                    errorLabel.Text = "鼠标快捷键需要至少一个修饰键（Ctrl / Alt / Shift）。";
                    return;
                }
                if (!linkBox.Checked && newMouse.ToString() == newKeyboard.ToString())
                {
                    errorLabel.Text = "键盘和鼠标的快捷键不能相同。";
                    return;
                }

                Host = newHost;
                Port = newPort;
                KeyboardHotkey = newKeyboard;
                MouseHotkey = newMouse;
                LinkMouse = linkBox.Checked;
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
            Controls.Add(hotkeyGroup);
            Controls.Add(errorLabel);
            Controls.Add(cancelButton);
            Controls.Add(saveButton);
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
        private readonly ToolStripMenuItem exitItem = new ToolStripMenuItem();
        private readonly Timer connectionTimer = new Timer();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        private TcpClient client;
        private NetworkStream stream;
        private string ip = "127.0.0.1";
        private int port = 4321;
        private int mouseHz = 250;
        private Hotkey keyboardHotkey = Hotkey.DefaultKeyboard();
        private Hotkey mouseHotkey = Hotkey.DefaultMouse();
        private bool linkMouse = true;
        private bool keyboardMode;
        private bool mouseMode;
        private bool configLoaded;
        private bool settingsOpen;
        private bool disposed;

        // 网络收发全部在后台线程执行。全局钩子也挂在 UI 线程上，如果在钩子回调里
        // 同步写网络，Tailscale 链路一旦抖动（中继切换/半死连接），Write 会阻塞
        // 几秒到几十秒：钩子回调超时，系统把按键鼠标放行给 Windows —— 表现就是
        // "两台电脑同时响应"、修饰键状态失步、切换热键失灵。改队列后 UI 线程只入队。
        private readonly Queue<string> pendingLines = new Queue<string>();
        private readonly object sendGate = new object();
        private System.Threading.Thread senderThread;
        private volatile bool senderRunning;
        private volatile bool connecting;
        private DateTime lastEnqueueUtc;

        internal BridgeApplicationContext()
        {
            LoadConfiguration();
            appIcon = LoadAppIcon();

            hotkeyForm.ShowInTaskbar = false;
            hotkeyForm.Visible = false;
            IntPtr unusedHandle = hotkeyForm.Handle;
            GC.KeepAlive(unusedHandle);
            hotkeyForm.KeyboardHotKeyPressed += delegate { ToggleKeyboardMode(); };
            hotkeyForm.MouseHotKeyPressed += delegate { ToggleMouseMode(); };

            hook.Key += HookKey;
            hook.ToggleRequested += delegate { ToggleKeyboardMode(); };
            hook.Install();
            hook.Enabled = false;

            mouseHook.Moved += HookMouseMove;
            mouseHook.Button += HookMouseButton;
            mouseHook.Wheel += HookMouseWheel;
            mouseHook.Install();
            mouseHook.SetRate(mouseHz);
            mouseHook.Enabled = false;

            toggleItem.Click += delegate { ToggleKeyboardMode(); };
            mouseToggleItem.Click += delegate { ToggleMouseMode(); };
            settingsItem.Text = "设置...";
            settingsItem.Click += delegate { ShowSettings(false); };
            exitItem.Text = "退出 TypeBridge";
            exitItem.Click += delegate { ExitThread(); };
            trayMenu.Items.Add(toggleItem);
            trayMenu.Items.Add(mouseToggleItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(settingsItem);
            trayMenu.Items.Add(exitItem);

            tray.ContextMenuStrip = trayMenu;
            tray.Icon = appIcon;
            tray.DoubleClick += delegate { ToggleKeyboardMode(); };
            tray.Visible = true;

            connectionTimer.Interval = 1000;
            connectionTimer.Tick += delegate
            {
                ConnectToServer();
                Heartbeat();
            };
            connectionTimer.Start();

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
                        ip = value;
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
                    if (key == "keyboard_hotkey")
                        keyboardHotkey = Hotkey.Parse(value, keyboardHotkey);
                    if (key == "mouse_hotkey")
                        mouseHotkey = Hotkey.Parse(value, mouseHotkey);
                    if (key == "mouse_with_keyboard")
                        linkMouse = ParseBool(value, linkMouse);
                }
            }
            catch
            {
                // 配置不可读时继续使用默认值。
            }
        }

        private static bool ParseBool(string value, bool fallback)
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
            settingsOpen = true;

            bool resumeKeyboard = keyboardMode;
            bool resumeMouse = mouseMode;

            // 设置窗口要用本机键盘操作，先释放钩子和全局热键，否则按键会被发去 Ubuntu、
            // 正在使用的组合键也无法录制。
            hook.Enabled = false;
            mouseHook.ReleaseAll();
            mouseHook.Enabled = false;
            mouseHook.RestoreCursor();
            mouseMode = false;
            hotkeyForm.SuspendHotkeys();
            UpdateStatus();

            try
            {
                using (SettingsForm form = new SettingsForm(
                    ip, port, keyboardHotkey, mouseHotkey, linkMouse, firstRun))
                {
                    if (form.ShowDialog() != DialogResult.OK)
                        return;

                    ip = form.Host;
                    port = form.Port;
                    keyboardHotkey = form.KeyboardHotkey;
                    mouseHotkey = form.MouseHotkey;
                    linkMouse = form.LinkMouse;

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
                SetKeyboardMode(resumeKeyboard, false);
                if (linkMouse ? resumeKeyboard : resumeMouse)
                    SetMouseMode(true, false);
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
                        "mouse_with_keyboard=" + (linkMouse ? "1" : "0")
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
            bool target = !keyboardMode;
            if (target && stream == null)
            {
                ShowTrayTip("未切换", "尚未连接 Ubuntu，请检查地址和网络");
                return;
            }
            if (linkMouse)
            {
                SetKeyboardMode(target, false);
                SetMouseMode(target, false);
                if (target)
                    ShowTrayTip("TypeBridge", "键盘 + 鼠标 → Ubuntu，按 " + keyboardHotkey + " 切回");
                else
                    ShowTrayTip("TypeBridge", "键盘 + 鼠标 → Windows");
            }
            else
            {
                SetKeyboardMode(target, true);
            }
        }

        private void ToggleMouseMode()
        {
            if (linkMouse)
                return;
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
            keyboardMode = remote;
            hook.Enabled = remote;
            UpdateStatus();
            if (notify)
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
            }
            else
            {
                mouseHook.ReleaseAll();
                mouseHook.Enabled = false;
                mouseHook.RestoreCursor();
            }

            mouseMode = remote;
            UpdateStatus();
            if (notify)
                ShowTrayTip("TypeBridge", remote ? "鼠标 → Ubuntu" : "鼠标 → Windows");
            return true;
        }

        private void UpdateStatus()
        {
            bool wasConnected = mouseHook.Connected;
            mouseHook.Connected = stream != null;
            if (mouseHook.Connected && !wasConnected && mouseMode && mouseHook.Enabled)
                mouseHook.ResetAnchor();

            if (linkMouse)
            {
                if (keyboardMode)
                {
                    tray.Text = stream != null
                        ? "TypeBridge → Ubuntu（" + (mouseMode ? "键盘 + 鼠标" : "仅键盘") + "，已连接）"
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
                    + " 鼠标:" + (mouseMode ? "Ubuntu" : "Windows");
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

        private void ConnectToServer()
        {
            if (stream != null || connecting)
                return;
            connecting = true;
            string host = ip;
            int targetPort = port;
            // 连接放到线程池：原来 Wait(400) 每秒阻塞 UI 线程 400ms，
            // 期间全局钩子回调超时，按键鼠标会漏进 Windows。
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                TcpClient candidate = null;
                bool ok = false;
                try
                {
                    candidate = new TcpClient();
                    // 小包即时发送，否则 Nagle 会攒到 ACK 才走，鼠标位移会明显发黏
                    candidate.NoDelay = true;
                    ok = candidate.ConnectAsync(host, targetPort).Wait(600);
                }
                catch
                {
                    ok = false;
                }
                if (!ok && candidate != null)
                {
                    try { candidate.Close(); }
                    catch { }
                }
                try
                {
                    hotkeyForm.BeginInvoke(new MethodInvoker(delegate
                    {
                        connecting = false;
                        if (!ok || stream != null || host != ip || targetPort != port)
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
                        // 写超时：半死链路上 Write 最多卡 4 秒就报错，
                        // 不至于把 UI 线程外的发送线程卡几十秒才判死
                        try { stream.WriteTimeout = 4000; }
                        catch { }
                        EnableKeepAlive(candidate);
                        StartSender();
                        UpdateStatus();
                    }));
                }
                catch { }
            });
        }

        /// <summary>5 秒空闲后每 2 秒发 TCP 探测包，死链路几秒内触发写失败，
        /// 而不是等系统默认的探测周期（可达数分钟）。</summary>
        private static void EnableKeepAlive(TcpClient tcp)
        {
            try
            {
                byte[] values = new byte[12];
                BitConverter.GetBytes(1ul).CopyTo(values, 0);
                BitConverter.GetBytes(5000ul).CopyTo(values, 4);
                BitConverter.GetBytes(2000ul).CopyTo(values, 8);
                tcp.Client.IOControl(IOControlCode.KeepAliveValues, values, null);
            }
            catch { }
        }

        /// <summary>空闲超过 3 秒发一个 PING，既保活又让断链能被写操作尽快发现。</summary>
        private void Heartbeat()
        {
            lock (sendGate)
            {
                if (stream == null)
                    return;
                if ((DateTime.UtcNow - lastEnqueueUtc).TotalSeconds < 3)
                    return;
                pendingLines.Enqueue("PING");
                lastEnqueueUtc = DateTime.UtcNow;
            }
        }

        private void StartSender()
        {
            senderRunning = false;
            senderThread = new System.Threading.Thread(SenderLoop);
            senderThread.IsBackground = true;
            senderThread.Name = "TypeBridge-Sender";
            senderRunning = true;
            senderThread.Start();
        }

        private void SenderLoop()
        {
            while (senderRunning)
            {
                string line = null;
                lock (sendGate)
                {
                    if (pendingLines.Count > 0)
                        line = pendingLines.Dequeue();
                }
                if (line == null)
                {
                    System.Threading.Thread.Sleep(2);
                    continue;
                }
                NetworkStream target = stream;
                if (target == null)
                    continue;
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                    target.Write(bytes, 0, bytes.Length);
                    target.Flush();
                }
                catch
                {
                    NotifyConnectionLost();
                    return;
                }
            }
        }

        /// <summary>发送线程发现断链后回到 UI 线程收尾：断开并把键鼠切回 Windows。
        /// 这是兜底保护——即使切换热键在断链期间失灵，用户也不会被卡在 Ubuntu 模式。</summary>
        private void NotifyConnectionLost()
        {
            try
            {
                hotkeyForm.BeginInvoke(new MethodInvoker(delegate
                {
                    bool wasActive = keyboardMode || mouseMode;
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

        private void CloseConnection()
        {
            // 先补发鼠标 up，再断开，避免 Ubuntu 端卡在拖拽状态
            mouseHook.ReleaseAll();
            try
            {
                if (client != null)
                    client.Close();
            }
            catch { }
            client = null;
            stream = null;
            senderRunning = false;
            lock (sendGate)
            {
                pendingLines.Clear();
            }
            mouseHook.Connected = false;
            mouseHook.RestoreCursor();
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
