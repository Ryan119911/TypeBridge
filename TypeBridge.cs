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
[assembly: AssemblyVersion("3.3.0.0")]

namespace TypeBridgeApp
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

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

        internal static void LogInfo(string message)
        {
            try
            {
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "typebridge-error.log");
                System.IO.File.AppendAllText(
                    path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " INFO " + message + "\r\n");
            }
            catch { }
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
                    capsLockState = (GetKeyState(0x14) & 1) != 0;
            }
        }

        internal event EventHandler<BridgeKeyEventArgs> Key;
        internal event EventHandler ToggleRequested;

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

        private List<string> ActiveModifiers()
        {
            List<string> modifiers = new List<string>();
            if (modifierDown.Contains(0xA2) || modifierDown.Contains(0xA3))
                modifiers.Add("ctrl");
            if (modifierDown.Contains(0xA4) || modifierDown.Contains(0xA5))
                modifiers.Add("alt");
            if (modifierDown.Contains(0xA0) || modifierDown.Contains(0xA1))
                modifiers.Add("shift");
            if (modifierDown.Contains(0x5B) || modifierDown.Contains(0x5C))
                modifiers.Add("super");
            return modifiers;
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

        private static bool IsToggleHotKey(int virtualKey, List<string> modifiers)
        {
            return virtualKey == (int)Keys.U &&
                modifiers.Count == 2 &&
                modifiers.Contains("ctrl") &&
                modifiers.Contains("alt");
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

                    if (IsModifierKey(vk))
                    {
                        if (modifierDown.Add(vk))
                            suppressedKeys.Add(vk);
                        return (IntPtr)1;
                    }

                    List<string> modifiers = ActiveModifiers();
                    if (IsToggleHotKey(vk, modifiers))
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
        private const int FlushIntervalMs = 16;
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

        private readonly HookProc callback;
        private readonly Timer flushTimer = new Timer();
        private readonly HashSet<int> pressedButtons = new HashSet<int>();
        private IntPtr hook = IntPtr.Zero;
        private int anchorX;
        private int anchorY;
        private int pendingDx;
        private int pendingDy;

        internal bool Enabled { get; set; }
        internal bool Connected { get; set; }

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
            get { return Enabled && Connected && hook != IntPtr.Zero; }
        }

        internal void Install()
        {
            if (hook != IntPtr.Zero)
                return;
            hook = SetWindowsHookEx(WhMouseLl, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            flushTimer.Start();
        }

        /// <summary>接管瞬间把光标钉到屏幕中心，避免锚点贴近屏幕边缘丢位移。</summary>
        internal void ResetAnchor()
        {
            pendingDx = 0;
            pendingDy = 0;
            SetCursorPos(anchorX, anchorY);
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
        private readonly Label errorLabel = new Label();

        internal string Host { get; private set; }
        internal int Port { get; private set; }

        internal SettingsForm(string host, int port, bool firstRun)
        {
            Host = host;
            Port = port;

            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

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
        private readonly GlobalMouseHook mouseHook = new GlobalMouseHook();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ToastForm toast = new ToastForm();
        private readonly Icon appIcon;        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
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
            appIcon = LoadAppIcon();

            hotkeyForm.ShowInTaskbar = false;
            hotkeyForm.Visible = false;
            IntPtr unusedHandle = hotkeyForm.Handle;
            GC.KeepAlive(unusedHandle);
            hotkeyForm.HotKeyPressed += delegate { ToggleMode(); };

            hook.Key += HookKey;
            hook.ToggleRequested += delegate { ToggleMode(); };
            hook.Install();
            hook.Enabled = false;

            mouseHook.Moved += HookMouseMove;
            mouseHook.Button += HookMouseButton;
            mouseHook.Wheel += HookMouseWheel;
            mouseHook.Install();
            mouseHook.Enabled = false;

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
            tray.Icon = appIcon;
            tray.DoubleClick += delegate { ToggleMode(); };
            tray.Visible = true;

            connectionTimer.Interval = 1000;
            connectionTimer.Tick += delegate { ConnectToServer(); };
            connectionTimer.Start();

            SetMode(false, false);
            ShowTrayTip("TypeBridge 已启动", "Ctrl + Alt + U 切换到 Ubuntu（键盘 + 鼠标）");
            if (!hotkeyForm.HotKeyAvailable)
                ShowTrayTip("快捷键不可用", "Ctrl+Alt+U 已被其他程序占用，请用托盘菜单切换");
            if (!configLoaded)
            {
                hotkeyForm.BeginInvoke(new Action(delegate { ShowSettings(true); }));
            }
            Program.LogInfo("started, icon=" + (appIcon == null ? "null" : "ok"));
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
            if (remote)
            {
                mouseHook.ResetAnchor();
            }
            else
            {
                mouseHook.ReleaseAll();
            }
            mouseHook.Enabled = remote;
            UpdateStatus();
            if (notify)
                ShowTrayTip("TypeBridge", "当前输入：" + (remote ? "Ubuntu（键盘 + 鼠标）" : "Windows"));
        }

        private void UpdateStatus()
        {
            mouseHook.Connected = stream != null;
            if (ubuntuMode)
            {
                tray.Text = stream != null ? "TypeBridge → Ubuntu（已连接）" : "TypeBridge → Ubuntu（未连接）";
                toggleItem.Text = "切换到 Windows\tCtrl+Alt+U";
            }
            else
            {
                tray.Text = "TypeBridge → Windows";
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

        private void HookMouseMove(object sender, BridgeMouseMoveEventArgs e)
        {
            if (!ubuntuMode)
                return;
            SendLine("M " + e.Dx.ToString() + " " + e.Dy.ToString());
        }

        private void HookMouseButton(object sender, BridgeMouseButtonEventArgs e)
        {
            if (!ubuntuMode)
                return;
            SendLine("B " + e.Button.ToString() + (e.Down ? " down" : " up"));
        }

        private void HookMouseWheel(object sender, BridgeMouseWheelEventArgs e)
        {
            if (!ubuntuMode)
                return;
            SendLine("W " + e.Direction);
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
            mouseHook.Connected = false;
            mouseHook.ReleaseAll();
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
            Program.LogInfo("ExitThreadCore");
            if (!disposed)
            {
                disposed = true;
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
