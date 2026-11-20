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

}
