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
        private struct CursorWarp
        {
            internal Point Position;
            internal int Tick;
        }
        private static readonly List<CursorWarp> cursorWarps = new List<CursorWarp>();

        internal static void SetPosition(Point position)
        {
            // SetCursorPos 产生的移动没有 LLMHF_INJECTED。先记下目标，钩子
            // 才能区分本程序挪指针和用户真的移动本机鼠标。
            lock (gate)
            {
                if (cursorWarps.Count >= 128) cursorWarps.RemoveAt(0);
                cursorWarps.Add(new CursorWarp { Position = position, Tick = Environment.TickCount });
            }
            try { Cursor.Position = position; }
            catch { }
        }

        internal static bool ConsumeCursorWarp(int x, int y)
        {
            lock (gate)
            {
                int now = Environment.TickCount;
                cursorWarps.RemoveAll(delegate(CursorWarp warp) { return unchecked(now - warp.Tick) > 500; });
                for (int i = cursorWarps.Count - 1; i >= 0; i--)
                {
                    if (cursorWarps[i].Position.X != x || cursorWarps[i].Position.Y != y) continue;
                    // Windows 可以合并连续的移动消息，消费匹配项及其之前的目标。
                    cursorWarps.RemoveRange(0, i + 1);
                    return true;
                }
                return false;
            }
        }

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
            SetPosition(new Point(x, y));
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

}
