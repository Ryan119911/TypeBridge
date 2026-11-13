using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace TypeBridgeApp
{
    /// <summary>
    /// 设置窗口（WPF）：矢量文字 + 真实投影/圆角，不再依赖 GDI+ 手绘。
    /// </summary>
    internal sealed class WpfSettingsWindow : Window
    {
        private static readonly Color Ink = Color.FromRgb(16, 22, 34);
        private static readonly Color Sub = Color.FromRgb(82, 96, 118);
        private static readonly Color Line = Color.FromRgb(230, 236, 246);
        private static readonly Color FieldBg = Color.FromRgb(246, 249, 254);
        private static readonly Color Accent = Color.FromRgb(61, 126, 240);

        private readonly TextBox hostBox = new TextBox();
        private readonly TextBox portBox = new TextBox();
        private readonly TextBox recvBox = new TextBox();
        private readonly TextBox remoteBox = new TextBox();
        private HotkeyCapture kbCapture;
        private HotkeyCapture mouseCapture;
        private HotkeyCapture activeCapture;
        private readonly Switch linkSwitch = new Switch();
        private readonly ScreenArranger arranger = new ScreenArranger();
        private readonly TextBlock errorText = new TextBlock();
        private readonly string initialEdge;

        private bool linkMouse;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal string Host { get; private set; }
        internal int Port { get; private set; }
        internal Hotkey KeyboardHotkey { get; private set; }
        internal Hotkey MouseHotkey { get; private set; }
        internal string RecvDir { get; private set; }
        internal string RemoteDir { get; private set; }
        internal string SwitchEdge { get { return arranger.Edge; } }
        internal bool LinkMouse { get { return linkMouse; } }

        /// <summary>只接受守护进程认识的四个方向，缺省与 BridgeApplicationContext 一致（Ubuntu 在右）。</summary>
        private static string NormalizeEdge(string value)
        {
            string lower = (value ?? string.Empty).Trim().ToLowerInvariant();
            return lower == "left" || lower == "right" || lower == "top" || lower == "bottom"
                ? lower : "right";
        }

        internal WpfSettingsWindow(string host, int port, Hotkey keyboardHotkey, Hotkey mouseHotkey,
            bool linkMouseValue, string switchEdgeValue, string recvDirValue, string remoteDirValue, bool firstRun)
        {
            Host = host;
            Port = port;
            KeyboardHotkey = keyboardHotkey;
            MouseHotkey = mouseHotkey;
            RecvDir = recvDirValue ?? string.Empty;
            RemoteDir = remoteDirValue ?? string.Empty;
            linkMouse = linkMouseValue;
            initialEdge = NormalizeEdge(switchEdgeValue);

            Title = firstRun ? "TypeBridge 首次设置" : "TypeBridge 设置";
            // 系统窗口：分层透明窗口会强制 WPF 用灰度抗锯齿，关掉它才能真正走 ClearType。
            // 标题栏由 WindowChrome 隐藏，阴影/圆角/边框交给 DWM；左右各留 6px 供系统拖宽。
            WindowStyle = WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;
            Background = new SolidColorBrush(Color.FromRgb(245, 248, 254));
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6, 0, 6, 0),
                UseAeroCaptionButtons = false
            });
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;   // 最小化后才能从任务栏恢复
            Topmost = true;
            SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height - 12;
            Width = 560;
            MinWidth = 470;
            MaxWidth = 920;

            // 文字清晰度：明确中文字体、小字号用 Display 模式对齐整像素、布局按整像素舍入。
            // 阴影单独成层，不给含文字的容器挂 Effect，否则整棵树会被光栅化。
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            kbCapture = new HotkeyCapture(ActivateCapture);
            kbCapture.SetValue(keyboardHotkey);
            mouseCapture = new HotkeyCapture(ActivateCapture);
            mouseCapture.SetValue(mouseHotkey);

            Content = BuildContent();
            PreviewKeyDown += OnWindowKeyDown;
        }

        /// <summary>Win11 下让 DWM 给系统窗口画圆角；Win10 不认识这个属性，直接忽略。</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            }
            catch { }
        }

        // ---------- 快捷键捕获 ----------

        private void ActivateCapture(HotkeyCapture capture)
        {
            if (activeCapture != null)
                activeCapture.IsActive = false;
            activeCapture = capture;
            if (capture != null)
                capture.IsActive = true;
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (activeCapture == null)
            {
                if (e.Key == Key.Escape) { DialogResult = false; Close(); }
                return;
            }

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                ActivateCapture(null);
                e.Handled = true;
                return;
            }

            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt
                || key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin)
            {
                e.Handled = true;
                return;
            }

            ModifierKeys mods = Keyboard.Modifiers;

            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (mods == ModifierKeys.None)
            {
                // 无修饰键只放行安全整键（ScrollLock / Pause / Menu / F13–F24）。
                // 普通键盘没有 F13–F24，但前三个键都存在且几乎不被软件占用。
                if (!Hotkey.IsSafeBareKey((System.Windows.Forms.Keys)vk))
                {
                    e.Handled = true;
                    return;
                }
                activeCapture.SetValue(new Hotkey(false, false, false, false, (System.Windows.Forms.Keys)vk));
                ActivateCapture(null);
                e.Handled = true;
                return;
            }

            activeCapture.SetValue(new Hotkey((mods & ModifierKeys.Control) != 0, (mods & ModifierKeys.Alt) != 0,
                (mods & ModifierKeys.Shift) != 0, (mods & ModifierKeys.Windows) != 0, (System.Windows.Forms.Keys)vk));
            ActivateCapture(null);
            e.Handled = true;
        }

        // ---------- 结构 ----------

        private UIElement BuildContent()
        {
            Border shell = new Border
            {
                // 系统窗口自带圆角与投影，内容铺满即可；四角由 DWM 裁成圆角
                Margin = new Thickness(0),
                Background = new LinearGradientBrush(
                    Color.FromRgb(255, 255, 255), Color.FromRgb(240, 245, 253), new Point(0, 0), new Point(0.3, 1))
            };
            shell.MouseLeftButtonDown += delegate { try { DragMove(); } catch { } };

            StackPanel stack = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };

            stack.Children.Add(BuildHeader());
            stack.Children.Add(BuildAddressCard());

            StackPanel hotkeyBody = new StackPanel();
            UIElement hotkeyCard = Card("切换快捷键", hotkeyBody);
            hotkeyBody.Children.Add(BuildLinkRow());
            hotkeyBody.Children.Add(Row("键盘快捷键", kbCapture, 150));
            hotkeyBody.Children.Add(Row("鼠标快捷键", mouseCapture, 150));
            hotkeyBody.Children.Add(Note("点输入框后按下组合键，至少含 Ctrl / Alt / Shift；也可单独用 ScrollLock / Pause / Menu"));
            stack.Children.Add(hotkeyCard);

            StackPanel layoutBody = new StackPanel();
            UIElement layoutCard = Card("屏幕排列", layoutBody);
            arranger.Edge = initialEdge;
            layoutBody.Children.Add(arranger);
            TextBlock hint = Note("按住 Ubuntu 方块拖到本机的任意一侧，靠近时自动吸附，松手生效");
            hint.Margin = new Thickness(0, 8, 0, 0);
            layoutBody.Children.Add(hint);
            stack.Children.Add(layoutCard);

            StackPanel fileBody = new StackPanel();
            UIElement fileCard = Card("文件路径", fileBody);
            fileBody.Children.Add(Label("从 Ubuntu 收到的文件保存到"));
            Grid recvRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            recvRow.ColumnDefinitions.Add(new ColumnDefinition());
            recvRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            recvBox.Text = RecvDir;
            recvRow.Children.Add(Field(recvBox));
            Button browse = SecondaryButton("浏览");
            browse.Margin = new Thickness(8, 0, 0, 0);
            browse.Click += delegate
            {
                using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = "选择从 Ubuntu 收到的文件保存位置";
                    if (recvBox.Text.Length > 0 && System.IO.Directory.Exists(recvBox.Text))
                        dialog.SelectedPath = recvBox.Text;
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        recvBox.Text = dialog.SelectedPath;
                }
            };
            Grid.SetColumn(browse, 1);
            recvRow.Children.Add(browse);
            fileBody.Children.Add(recvRow);
            fileBody.Children.Add(Label("发送到 Ubuntu 的目录（留空 = Ubuntu 桌面）"));
            remoteBox.Text = RemoteDir;
            fileBody.Children.Add(Field(remoteBox));
            stack.Children.Add(fileCard);

            errorText.Foreground = new SolidColorBrush(Color.FromRgb(198, 52, 52));
            errorText.FontSize = 12;
            errorText.Margin = new Thickness(2, 2, 0, 0);
            errorText.Visibility = Visibility.Collapsed;
            stack.Children.Add(errorText);

            // 卡片区可滚动，页脚固定常驻，避免保存按钮被挤出屏幕
            ScrollViewer scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            };
            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(scroller);

            UIElement footer = BuildFooter();
            Grid.SetRow(footer, 1);
            layout.Children.Add(footer);

            shell.Child = layout;

            // 外层阴影改由 DWM 绘制，这里不再放自绘阴影层
            Grid root = new Grid();
            root.Children.Add(shell);

            // 右下角给一个轻量的"可拉宽"提示，不吃鼠标事件
            TextBlock resizeHint = new TextBlock
            {
                Text = "◢",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(176, 190, 212)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 8, 6),
                IsHitTestVisible = false
            };
            root.Children.Add(resizeHint);
            root.Children.Add(MakeResizeGrip(true));
            root.Children.Add(MakeResizeGrip(false));
            return root;
        }

        /// <summary>无边框窗口左右各放一条透明 grip，拖它改宽度（高度仍随内容自适应）。</summary>
        private Thumb MakeResizeGrip(bool leftEdge)
        {
            Thumb grip = new Thumb
            {
                Width = 6,
                HorizontalAlignment = leftEdge ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Opacity = 0,
                Cursor = Cursors.SizeWE,
                IsTabStop = false
            };
            grip.DragDelta += delegate(object s, DragDeltaEventArgs e)
            {
                double change = leftEdge ? -e.HorizontalChange : e.HorizontalChange;
                double target = Math.Max(MinWidth, Math.Min(MaxWidth, ActualWidth + change));
                if (Math.Abs(target - ActualWidth) < 0.5) return;
                if (leftEdge) Left += ActualWidth - target;
                Width = target;
            };
            return grip;
        }

        private UIElement BuildHeader()
        {
            Grid header = new Grid { Margin = new Thickness(2, 0, 0, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titles = new StackPanel();
            titles.Children.Add(new TextBlock
            {
                Text = "TypeBridge",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Ink)
            });
            titles.Children.Add(new TextBlock
            {
                Text = "Windows ⇄ Ubuntu 键鼠与文件",
                FontSize = 13,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Sub)
            });
            header.Children.Add(titles);

            Button close = new Button
            {
                Content = "✕",
                Width = 30,
                Height = 30,
                FontSize = 13,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Sub),
                Template = RoundButtonTemplate(0)
            };
            close.ToolTip = "取消并关闭";
            close.Click += delegate { DialogResult = false; Close(); };
            AddChromeButtonHover(close);

            Button minimize = new Button
            {
                Content = "—",
                Width = 30,
                Height = 30,
                FontSize = 13,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Sub),
                Template = RoundButtonTemplate(0),
                ToolTip = "最小化",
                Margin = new Thickness(0, 0, 2, 0)
            };
            minimize.Click += delegate { WindowState = WindowState.Minimized; };
            AddChromeButtonHover(minimize);

            StackPanel windowButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            windowButtons.Children.Add(minimize);
            windowButtons.Children.Add(close);
            Grid.SetColumn(windowButtons, 1);
            header.Children.Add(windowButtons);
            return header;
        }







        private UIElement BuildAddressCard()
        {
            StackPanel card = new StackPanel();
            UIElement cardHost = Card("Ubuntu 地址", card);

            Grid addressRow = new Grid();
            addressRow.ColumnDefinitions.Add(new ColumnDefinition());
            addressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            hostBox.Text = Host;
            addressRow.Children.Add(Field(hostBox));

            Button detect = SecondaryButton("测一下");
            detect.Margin = new Thickness(8, 0, 0, 0);
            detect.Click += delegate
            {
                string target = hostBox.Text.Trim();
                if (target.Length == 0) { Fail("先填地址"); return; }
                string[] candidates = target.Split(new char[] { ',', ';', ' ', '，', '；' },
                    StringSplitOptions.RemoveEmptyEntries);
                detect.IsEnabled = false;
                detect.Content = "连接中";
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    bool ok = false;
                    string hit = null;
                    foreach (string candidate in candidates)
                    {
                        try
                        {
                            using (System.Net.Sockets.TcpClient probe = new System.Net.Sockets.TcpClient())
                            {
                                var task = probe.ConnectAsync(candidate, 4321);
                                if (task.Wait(1500) && probe.Connected) { ok = true; hit = candidate; break; }
                            }
                        }
                        catch { }
                    }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        detect.IsEnabled = true;
                        detect.Content = "测一下";
                        if (ok) { errorText.Visibility = Visibility.Collapsed; }
                        else { Fail("连不上 " + target + ":4321"); }
                    }));
                });
            };
            Grid.SetColumn(detect, 1);
            addressRow.Children.Add(detect);

            card.Children.Add(Label("多个地址用逗号分隔，自动连延迟最低的那个"));
            card.Children.Add(addressRow);

            Grid portRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            portRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            portRow.ColumnDefinitions.Add(new ColumnDefinition());
            portBox.Text = Port.ToString(CultureInfo.InvariantCulture);
            portRow.Children.Add(Field(portBox));

            TextBlock portHint = new TextBlock
            {
                Text = "默认 4321",
                FontSize = 12,
                Foreground = new SolidColorBrush(Sub),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(portHint, 1);
            portRow.Children.Add(portHint);

            card.Children.Add(Label("端口"));
            card.Children.Add(portRow);
            return cardHost;
        }

        private UIElement BuildLinkRow()
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            linkSwitch.IsOn = linkMouse;
            linkSwitch.Toggled += delegate
            {
                linkMouse = linkSwitch.IsOn;
                mouseCapture.IsEnabled = !linkMouse;
                mouseCapture.Opacity = linkMouse ? 0.45 : 1.0;
            };
            row.Children.Add(linkSwitch);

            TextBlock text = new TextBlock
            {
                Text = "键盘和鼠标一起切换",
                FontSize = 14,
                Foreground = new SolidColorBrush(Ink),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            mouseCapture.IsEnabled = !linkMouse;
            mouseCapture.Opacity = linkMouse ? 0.45 : 1.0;
            return row;
        }

        private UIElement BuildFooter()
        {
            Grid footer = new Grid { Margin = new Thickness(22, 10, 22, 18) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Button cancel = SecondaryButton("取消");
            cancel.Width = 96;
            cancel.HorizontalAlignment = HorizontalAlignment.Right;
            cancel.Click += delegate { DialogResult = false; Close(); };

            Button save = new Button
            {
                Content = "保存",
                Width = 96,
                Height = 38,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Accent),
                BorderThickness = new Thickness(0),
                Template = RoundButtonTemplate()
            };
            AddHoverOpacity(save);
            save.Click += delegate { Save(); };
            save.Margin = new Thickness(10, 0, 0, 0);

            footer.Children.Add(cancel);
            Grid.SetColumn(cancel, 0);
            Grid.SetColumn(save, 1);
            footer.HorizontalAlignment = HorizontalAlignment.Stretch;
            footer.Children.Add(save);
            return footer;
        }

        // ---------- 保存 ----------

        private void Save()
        {
            errorText.Visibility = Visibility.Collapsed;

            string newHost = hostBox.Text.Trim();
            if (newHost.Length == 0) { Fail("请输入 Ubuntu 地址"); return; }

            int newPort;
            if (!int.TryParse(portBox.Text.Trim(), out newPort) || newPort < 1 || newPort > 65535)
            { Fail("端口必须是 1 到 65535 之间的数字"); return; }

            Hotkey newKeyboard = kbCapture.Value;
            if (newKeyboard == null || !newKeyboard.IsValidHotkey)
            { Fail("键盘快捷键需要至少一个修饰键（Ctrl / Alt / Shift），或使用 ScrollLock / Pause / Menu / F13–F24 这类无冲突整键"); return; }

            Hotkey newMouse = mouseCapture.Value ?? Hotkey.DefaultMouse();
            if (!newMouse.IsValidHotkey)
            { Fail("鼠标快捷键需要至少一个修饰键（Ctrl / Alt / Shift），或使用 ScrollLock / Pause / Menu / F13–F24 这类无冲突整键"); return; }

            Host = newHost;
            Port = newPort;
            KeyboardHotkey = newKeyboard;
            MouseHotkey = newMouse;
            RecvDir = recvBox.Text.Trim();
            RemoteDir = remoteBox.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void Fail(string message)
        {
            errorText.Text = message;
            errorText.Visibility = Visibility.Visible;
        }

        // ---------- 控件工厂 ----------

        private static UIElement Card(string title, StackPanel body)
        {
            body.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(52, 66, 92)),
                Margin = new Thickness(0, 0, 0, 12)
            });

            Grid host = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            Border shadowLayer = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Brushes.White,
                Effect = Shadow(18, 3, 0.08)
            };
            Border card = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Brushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Line),
                Padding = new Thickness(16, 12, 16, 14),
                Child = body
            };
            host.Children.Add(shadowLayer);
            host.Children.Add(card);
            return host;
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = new SolidColorBrush(Sub),
                Margin = new Thickness(0, 0, 0, 6)
            };
        }

        private static TextBlock Note(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
        }

        private static UIElement Field(TextBox box)
        {
            Border frame = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(FieldBg),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 233, 245)),
                Padding = new Thickness(10, 7, 10, 7)
            };
            box.BorderThickness = new Thickness(0);
            box.Background = Brushes.Transparent;
            box.FontSize = 14;
            box.Foreground = new SolidColorBrush(Ink);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            frame.Child = box;
            return frame;
        }

        private static UIElement Row(string label, UIElement control, double labelWidth)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            TextBlock text = new TextBlock
            {
                Text = label,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(52, 66, 92)),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(text);
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }

        private static Button SecondaryButton(string text)
        {
            Button button = new Button
            {
                Content = text,
                Height = 38,
                Padding = new Thickness(16, 0, 16, 0),
                FontSize = 13,
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Color.FromRgb(58, 72, 96)),
                Background = Brushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(216, 224, 238)),
                Template = RoundButtonTemplate()
            };
            AddHoverOpacity(button);
            return button;
        }

        private static ControlTemplate RoundButtonTemplate(double contentMargin = 16)
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(contentMargin, 0, contentMargin, 0));
            border.AppendChild(presenter);

            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        private static void AddHoverOpacity(Button button)
        {
            Style style = new Style(typeof(Button));
            Trigger hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(OpacityProperty, 0.82));
            style.Triggers.Add(hover);
            button.Style = style;
        }

        /// <summary>标题栏按钮默认透明，靠悬停底色提示可点击。</summary>
        private static void AddChromeButtonHover(Button button)
        {
            Style style = new Style(typeof(Button));
            Trigger hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(226, 233, 245))));
            style.Triggers.Add(hover);
            button.Style = style;
        }

        private static DropShadowEffect Shadow(double blur, double depth, double opacity)
        {
            return new DropShadowEffect
            {
                BlurRadius = blur,
                ShadowDepth = depth,
                Direction = 270,
                Opacity = opacity,
                Color = Color.FromRgb(23, 43, 77)
            };
        }


        // ---------- 内部控件 ----------

        /// <summary>按键捕获框：点进去按组合键即可。</summary>
        private sealed class HotkeyCapture : Border
        {
            private readonly TextBlock text = new TextBlock
            {
                FontSize = 13,
                Foreground = new SolidColorBrush(Ink),
                VerticalAlignment = VerticalAlignment.Center
            };

            internal Hotkey Value { get; set; }
            internal bool IsActive
            {
                set
                {
                    BorderBrush = new SolidColorBrush(value ? Accent : Color.FromRgb(226, 233, 245));
                    BorderThickness = new Thickness(value ? 1.6 : 1);
                }
            }

            internal HotkeyCapture(Action<HotkeyCapture> activate)
            {
                CornerRadius = new CornerRadius(9);
                Background = new SolidColorBrush(FieldBg);
                BorderThickness = new Thickness(1);
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 233, 245));
                Padding = new Thickness(10, 7, 10, 7);
                Cursor = Cursors.Hand;
                Child = text;

                MouseLeftButtonDown += delegate
                {
                    if (activate != null) activate(this);
                    if (Value == null) text.Text = "按下组合键…";
                };
            }

            internal void SetValue(Hotkey value)
            {
                Value = value;
                Refresh();
            }

            private void Refresh()
            {
                text.Text = Value == null ? "未设置" : Value.ToString();
                text.Foreground = new SolidColorBrush(Value == null ? Sub : Ink);
            }

        }

        /// <summary>胶囊开关。</summary>
        private sealed class Switch : Grid
        {
            private readonly Border track = new Border
            {
                Width = 46,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Color.FromRgb(214, 222, 236))
            };
            private readonly Ellipse knob = new Ellipse
            {
                Width = 20,
                Height = 20,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(3, 0, 0, 0),
                Effect = Shadow(6, 1, 0.25)
            };

            private bool on;
            internal event EventHandler Toggled;

            internal bool IsOn
            {
                get { return on; }
                set { on = value; Apply(false); }
            }

            internal Switch()
            {
                Width = 46;
                Height = 26;
                Cursor = Cursors.Hand;
                VerticalAlignment = VerticalAlignment.Center;
                Children.Add(track);
                Children.Add(knob);
                MouseLeftButtonUp += delegate
                {
                    on = !on;
                    Apply(true);
                    EventHandler handler = Toggled;
                    if (handler != null) handler(this, EventArgs.Empty);
                };
            }

            private void Apply(bool animate)
            {
                Color target = on ? Accent : Color.FromRgb(214, 222, 236);
                if (animate)
                {
                    ColorAnimation color = new ColorAnimation(target, TimeSpan.FromMilliseconds(140));
                    track.Background = new SolidColorBrush(((SolidColorBrush)track.Background).Color);
                    track.Background.BeginAnimation(SolidColorBrush.ColorProperty, color);
                }
                else
                {
                    track.Background = new SolidColorBrush(target);
                }

                double shift = on ? 20 : 0;
                if (animate)
                {
                    TranslateTransform transform = knob.RenderTransform as TranslateTransform ?? new TranslateTransform();
                    knob.RenderTransform = transform;
                    transform.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(shift, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
                else
                {
                    knob.RenderTransform = new TranslateTransform(shift, 0);
                }
            }
        }

        /// <summary>两块屏幕的摆放区，可拖动吸附。</summary>
        /// <summary>
        /// 两块屏幕的摆放区，交互参照 Windows 的显示器排列：
        /// 按住 Ubuntu 方块随便拖，靠近某一侧会吸附，松手后按最近的一侧决定切屏方向。
        /// </summary>
        private sealed class ScreenArranger : Canvas
        {
            private const double BoxW = 132;
            private const double BoxH = 82;
            private const double Gap = 18;
            private const double CanvasH = 232;   // 固定高度：四个方向都得留出拖动空间
            private const double Magnet = 26;     // 离吸附位多近就自动吸上去
            private const double Padding = 4;

            private readonly Border windows = MakeBox(true);
            private readonly Border ubuntu = MakeBox(false);
            private string edge = "right";
            private bool dragging;
            private Point grab;                   // 按下时鼠标相对方块左上角的偏移

            internal event Action Changed;

            private double ViewWidth
            {
                get { return ActualWidth > 1 ? ActualWidth : 400; }
            }

            internal string Edge
            {
                get { return edge; }
                set
                {
                    edge = NormalizeEdge(value);
                    Arrange();
                    Notify();
                }
            }

            internal ScreenArranger()
            {
                Height = CanvasH;
                MinWidth = 300;
                HorizontalAlignment = HorizontalAlignment.Stretch;
                Background = Brushes.Transparent;
                ClipToBounds = true;

                Children.Add(ubuntu);
                Children.Add(windows);

                ubuntu.Cursor = Cursors.Hand;
                ubuntu.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    dragging = true;
                    Point now = e.GetPosition(this);
                    grab = new Point(now.X - GetLeft(ubuntu), now.Y - GetTop(ubuntu));
                    ubuntu.CaptureMouse();
                    e.Handled = true;
                };
                ubuntu.MouseMove += delegate(object s, MouseEventArgs e)
                {
                    if (!dragging)
                        return;
                    DragTo(e.GetPosition(this));
                };
                ubuntu.MouseLeftButtonUp += delegate
                {
                    if (!dragging)
                        return;
                    dragging = false;
                    ubuntu.ReleaseMouseCapture();
                    CommitDrop();
                };

                SizeChanged += delegate(object s, SizeChangedEventArgs e)
                {
                    if (e.WidthChanged)
                        Arrange();
                };

                Arrange();
            }

            // Canvas 不按子元素算尺寸，必须自己给宽高，否则会被父容器压成 0。
            protected override Size MeasureOverride(Size constraint)
            {
                base.MeasureOverride(constraint);
                double width = double.IsInfinity(constraint.Width) ? 400 : constraint.Width;
                return new Size(Math.Max(width, BoxW * 2 + Gap + 12), CanvasH);
            }

            /// <summary>把方块拖到光标处（鼠标移动与自动化测试共用同一套逻辑）。</summary>
            private void DragTo(Point cursor)
            {
                double x = Clamp(cursor.X - grab.X, Padding, ViewWidth - BoxW - Padding);
                double y = Clamp(cursor.Y - grab.Y, Padding, CanvasH - BoxH - Padding);

                // 靠到某个摆位上就吸过去；吸附后重算抓取偏移，方块才不会跟手打架
                double snapX, snapY;
                NearestSide(x, y, out snapX, out snapY);
                double offsetX = x - snapX;
                double offsetY = y - snapY;
                if (Math.Sqrt(offsetX * offsetX + offsetY * offsetY) < Magnet)
                {
                    x = snapX;
                    y = snapY;
                    grab = new Point(cursor.X - x, cursor.Y - y);
                }

                SetLeft(ubuntu, x);
                SetTop(ubuntu, y);
            }

            /// <summary>松手：按离得最近的一侧落位。</summary>
            private void CommitDrop()
            {
                edge = NearestSide(GetLeft(ubuntu), GetTop(ubuntu));
                Arrange();
                Notify();
            }

            private void Notify()
            {
                Action handler = Changed;
                if (handler != null)
                    handler();
            }

            private static double Clamp(double value, double min, double max)
            {
                if (max < min)
                    max = min;
                return value < min ? min : (value > max ? max : value);
            }

            /// <summary>某一侧的规范摆位（返回 Ubuntu 方块的左上角坐标）。</summary>
            private void Canonical(string side, out double x, out double y)
            {
                double width = ViewWidth;
                double pairX = Math.Max(Padding, (width - (BoxW * 2 + Gap)) / 2);
                double pairY = (CanvasH - (BoxH * 2 + Gap)) / 2;
                double centerX = Math.Max(Padding, (width - BoxW) / 2);
                double centerY = (CanvasH - BoxH) / 2;

                switch (side)
                {
                    case "left":
                        x = pairX; y = centerY; break;
                    case "right":
                        x = pairX + BoxW + Gap; y = centerY; break;
                    case "top":
                        x = centerX; y = pairY; break;
                    default:
                        x = centerX; y = pairY + BoxH + Gap; break;
                }
            }

            private string NearestSide(double x, double y, out double snapX, out double snapY)
            {
                string best = edge;
                double bestDistance = double.MaxValue;
                double bx = x;
                double by = y;
                string[] sides = new string[] { "left", "right", "top", "bottom" };
                foreach (string side in sides)
                {
                    double cx, cy;
                    Canonical(side, out cx, out cy);
                    double dx = x - cx;
                    double dy = y - cy;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = side;
                        bx = cx;
                        by = cy;
                    }
                }
                snapX = bx;
                snapY = by;
                return best;
            }

            private string NearestSide(double x, double y)
            {
                double snapX, snapY;
                return NearestSide(x, y, out snapX, out snapY);
            }

            private void Arrange()
            {
                double width = ViewWidth;
                double pairX = Math.Max(Padding, (width - (BoxW * 2 + Gap)) / 2);
                double pairY = (CanvasH - (BoxH * 2 + Gap)) / 2;
                double centerX = Math.Max(Padding, (width - BoxW) / 2);
                double centerY = (CanvasH - BoxH) / 2;

                double winX = centerX, winY = centerY, ubuX = centerX, ubuY = centerY;
                switch (edge)
                {
                    case "left":
                        ubuX = pairX; winX = pairX + BoxW + Gap; break;
                    case "right":
                        winX = pairX; ubuX = pairX + BoxW + Gap; break;
                    case "top":
                        ubuY = pairY; winY = pairY + BoxH + Gap; break;
                    default:
                        winY = pairY; ubuY = pairY + BoxH + Gap; break;
                }

                SetLeft(windows, winX); SetTop(windows, winY);
                SetLeft(ubuntu, ubuX); SetTop(ubuntu, ubuY);
            }

            private static Border MakeBox(bool isWindows)
            {
                Brush background = isWindows
                    ? new LinearGradientBrush(Color.FromRgb(64, 132, 232), Color.FromRgb(30, 74, 156), 90)
                    : new LinearGradientBrush(Color.FromRgb(233, 84, 32), Color.FromRgb(184, 62, 22), 90);

                StackPanel content = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                content.Children.Add(new TextBlock
                {
                    Text = isWindows ? "▦" : "▤",
                    FontSize = 30,
                    Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                content.Children.Add(new TextBlock
                {
                    Text = isWindows ? "Windows" : "Ubuntu",
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 0),
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                return new Border
                {
                    Width = BoxW,
                    Height = BoxH,
                    CornerRadius = new CornerRadius(10),
                    Background = background,
                    Child = content,
                    Cursor = isWindows ? Cursors.Arrow : Cursors.SizeAll
                };
            }
        }
    }
}
