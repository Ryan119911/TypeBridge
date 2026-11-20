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
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);
        private bool highResolutionTimer;
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
                highResolutionTimer = timeBeginPeriod(1) == 0;
                flushTimer.Start();
                return;
            }
            // 停表前把残留位移发出去：此刻 Enabled/Connected 刚被关掉，
            // Active 已是 false，普通 Flush 会先清零再直接 return，最后一小段位移被吞掉。
            Flush(true);
            flushTimer.Stop();
            if (highResolutionTimer)
            {
                timeEndPeriod(1);
                highResolutionTimer = false;
            }
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

}
