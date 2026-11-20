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
}
