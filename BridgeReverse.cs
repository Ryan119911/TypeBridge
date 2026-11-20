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
    internal sealed partial class BridgeApplicationContext
    {
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
                SetKeyboardMode(false, false);
                SetMouseMode(false, false);
            }
            StartRemoteDrive(yNorm);
        }

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

    }
}
