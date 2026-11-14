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
        private readonly Switch edgeSwitchToggle = new Switch();
        private readonly ScreenArranger arranger = new ScreenArranger();
        private readonly TextBlock errorText = new TextBlock();
        private readonly string initialEdge;
        private readonly string initialOffset;

        private bool linkMouse;
        private bool edgeSwitch;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal string Host { get; private set; }
        internal int Port { get; private set; }
        internal Hotkey KeyboardHotkey { get; private set; }
        internal Hotkey MouseHotkey { get; private set; }
        internal string RecvDir { get; private set; }
        internal string RemoteDir { get; private set; }
        internal string SwitchEdge { get { return arranger.Edge; } }

        /// <summary>方块相对本机的偏移（"dx,dy"），用于把摆放位置存进配置。</summary>
        internal string LayoutOffset { get { return arranger.OffsetText; } }
        internal bool LinkMouse { get { return linkMouse; } }

        /// <summary>边缘切屏总开关，对应 ini 里的 edge_switch。</summary>
        internal bool EdgeSwitch { get { return edgeSwitch; } }

        /// <summary>只接受守护进程认识的四个方向，缺省与 BridgeApplicationContext 一致（Ubuntu 在右）。</summary>
        private static string NormalizeEdge(string value)
        {
            string lower = (value ?? string.Empty).Trim().ToLowerInvariant();
            return lower == "left" || lower == "right" || lower == "top" || lower == "bottom"
                ? lower : "right";
        }

        internal WpfSettingsWindow(string host, int port, Hotkey keyboardHotkey, Hotkey mouseHotkey,
            bool linkMouseValue, bool edgeSwitchValue, string switchEdgeValue,
            string recvDirValue, string remoteDirValue,
            bool firstRun, string layoutOffsetValue)
        {
            Host = host;
            Port = port;
            KeyboardHotkey = keyboardHotkey;
            MouseHotkey = mouseHotkey;
            RecvDir = recvDirValue ?? string.Empty;
            RemoteDir = remoteDirValue ?? string.Empty;
            linkMouse = linkMouseValue;
            edgeSwitch = edgeSwitchValue;
            initialEdge = NormalizeEdge(switchEdgeValue);
            initialOffset = layoutOffsetValue ?? string.Empty;

            Title = firstRun ? "TypeBridge 首次设置" : "TypeBridge 设置";
            // 普通窗口：标题栏、最小化/最大化/关闭、四边缩放全部交给系统，
            // 和任意 Windows 软件一样，按钮不会跟着内容滚走。
            WindowStyle = WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;
            Background = new SolidColorBrush(Color.FromRgb(245, 248, 254));
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;   // 最小化后才能从任务栏恢复
            Width = 560;
            Height = Math.Min(780, SystemParameters.WorkArea.Height - 80);
            MinWidth = 470;
            MinHeight = 400;
            MaxWidth = 920;
            MaxHeight = SystemParameters.WorkArea.Height - 12;

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

            // 拖动"屏幕排列"里的方块：挂在窗口级 Preview 事件上，
            // 这样不依赖画布/方块自己能不能收到鼠标事件（窗口一定能收到）。
            // 按住方块期间用定时器轮询光标位置来移动它：不依赖 MouseMove 事件
            // （按下并捕获鼠标后，实测移动事件并不会稳定送到窗口，方块就"拖不动"）。
            dragPoller.Interval = TimeSpan.FromMilliseconds(16);
            dragPoller.Tick += delegate { PollDrag(); };

            PreviewMouseDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Left)
                    return;
                if (arranger.BeginDrag(e.GetPosition(arranger)))
                {
                    e.Handled = true;
                    dragPoller.Start();
                }
            };
            PreviewMouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (!arranger.IsDragging)
                    return;
                dragPoller.Stop();
                arranger.EndDrag();
                e.Handled = true;
            };
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

        private readonly System.Windows.Threading.DispatcherTimer dragPoller =
            new System.Windows.Threading.DispatcherTimer();

        /// <summary>按住方块时每 16ms 取一次光标位置推着方块走。</summary>
        private void PollDrag()
        {
            if (!arranger.IsDragging)
            {
                dragPoller.Stop();
                return;
            }
            arranger.DragTo(System.Windows.Input.Mouse.GetPosition(arranger));
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
            StackPanel stack = new StackPanel { Margin = new Thickness(22, 16, 22, 20) };

            stack.Children.Add(new TextBlock
            {
                Text = "Windows ⇄ Ubuntu 键鼠与文件",
                FontSize = 12.5,
                Margin = new Thickness(2, 0, 0, 12),
                Foreground = new SolidColorBrush(Sub)
            });
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
            double offsetX, offsetY;
            if (TryParseOffset(initialOffset, out offsetX, out offsetY))
                arranger.SetOffset(offsetX, offsetY);
            layoutBody.Children.Add(arranger);
            TextBlock hint = Note("按住 Ubuntu 方块拖到任意位置：上下左右或四个角都行，靠近整齐位置会自动吸附");
            hint.Margin = new Thickness(0, 8, 0, 0);
            layoutBody.Children.Add(hint);

            // 边缘切屏总开关：以前只能改 ini 里的 edge_switch，设置页没入口
            edgeSwitchToggle.IsOn = edgeSwitch;
            edgeSwitchToggle.Toggled += delegate
            {
                edgeSwitch = edgeSwitchToggle.IsOn;
                arranger.Opacity = edgeSwitch ? 1.0 : 0.45;
            };
            arranger.Opacity = edgeSwitch ? 1.0 : 0.45;
            layoutBody.Children.Add(ToggleRow(edgeSwitchToggle,
                "启用边缘切屏", "鼠标被推过这一侧就切到 Ubuntu；关掉后只能用快捷键切"));
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

            return shell;
        }











        /// <summary>解析 "dx,dy" 形式的偏移，非法就返回 false。</summary>
        private static bool TryParseOffset(string text, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (string.IsNullOrEmpty(text))
                return false;
            string[] parts = text.Split(',');
            if (parts.Length != 2)
                return false;
            return double.TryParse(parts[0].Trim(), out x)
                && double.TryParse(parts[1].Trim(), out y);
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

        /// <summary>开关 + 标题 + 说明的一行，用于边缘切屏这类总开关。</summary>
        private static UIElement ToggleRow(Switch toggle, string title, string detail)
        {
            Grid row = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(toggle);

            StackPanel text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                Foreground = new SolidColorBrush(Ink)
            });
            TextBlock sub = Note(detail);
            sub.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(sub);

            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            return row;
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
            { Fail("键盘快捷键需要至少一个修饰键（Ctrl / Alt / Shift），或使用 ScrollLock / Pause / Menu 这类无冲突整键"); return; }

            Hotkey newMouse = mouseCapture.Value ?? Hotkey.DefaultMouse();
            if (!newMouse.IsValidHotkey)
            { Fail("鼠标快捷键需要至少一个修饰键（Ctrl / Alt / Shift），或使用 ScrollLock / Pause / Menu 这类无冲突整键"); return; }

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
        /// <summary>
        /// 两块屏幕的摆放区，交互参照 Windows 的显示器排列：
        /// 按住 Ubuntu 方块拖到任意位置（上、下、左、右和四个角都行），
        /// 靠近整齐位置时自动吸附，松手后按偏移的主方向决定切屏边。
        /// </summary>
        private sealed class ScreenArranger : Canvas
        {
            private const double BoxW = 132;
            private const double BoxH = 82;
            private const double Gap = 18;
            private const double CanvasH = 232;
            private const double Magnet = 18;      // 离整齐位置多近就吸过去
            private const double Padding = 4;

            private readonly Border windows = MakeBox(true);
            private readonly Border ubuntu = MakeBox(false);
            private string edge = "right";
            private double offsetX = BoxW + Gap;   // Ubuntu 相对 Windows 的偏移
            private double offsetY;
            private bool dragging;
            private bool dragLogged;
            private Point grab;

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
                    SetOffsetForEdge(edge);
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

                // 拖动由窗口级事件驱动（见 WpfSettingsWindow），这里只兜底捕获丢失
                LostMouseCapture += delegate
                {
                    if (!dragging)
                        return;
                    dragging = false;
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

            /// <summary>当前偏移，形如 "150,-100"。</summary>
            internal string OffsetText
            {
                get
                {
                    return offsetX.ToString("0") + "," + offsetY.ToString("0");
                }
            }

            /// <summary>直接设定偏移（用于读回上次保存的摆放位置）。</summary>
            internal void SetOffset(double dx, double dy)
            {
                offsetX = dx;
                offsetY = dy;
                CommitDrop();
            }

            internal bool IsDragging
            {
                get { return dragging; }
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

            /// <summary>按切屏方向给出标准偏移（只有四条边）。</summary>
            private void SetOffsetForEdge(string side)
            {
                double stepX = BoxW + Gap;
                double stepY = BoxH + Gap;
                offsetX = 0;
                offsetY = 0;
                switch (side)
                {
                    case "left": offsetX = -stepX; break;
                    case "right": offsetX = stepX; break;
                    case "top": offsetY = -stepY; break;
                    default: offsetY = stepY; break;
                }
            }

            /// <summary>Windows 方块的中心（画布坐标）。</summary>
            private Point WindowsCenter
            {
                get
                {
                    double width = ViewWidth;
                    return new Point(width / 2, CanvasH / 2);
                }
            }

            /// <summary>按下时调用：点在方块上就开始拖，返回是否接管了这次按下。</summary>
            internal bool BeginDrag(Point point)
            {
                double left = GetLeft(ubuntu);
                double top = GetTop(ubuntu);
                if (double.IsNaN(left) || double.IsNaN(top))
                    return false;
                if (point.X < left || point.X > left + BoxW || point.Y < top || point.Y > top + BoxH)
                    return false;

                dragging = true;
                dragLogged = false;
                grab = new Point(point.X - left, point.Y - top);
                CaptureMouse();
                Log.Write("arranger drag start point=" + point.X.ToString("0") + ","
                    + point.Y.ToString("0") + " box=" + left.ToString("0") + ","
                    + top.ToString("0") + " width=" + ActualWidth.ToString("0"));
                return true;
            }

            /// <summary>松手时调用：按主方向定切屏边，位置保持（已吸附）。</summary>
            internal void EndDrag()
            {
                if (!dragging)
                    return;
                dragging = false;
                ReleaseMouseCapture();
                CommitDrop();
                Log.Write("arranger drag end edge=" + edge
                    + " offset=" + offsetX.ToString("0") + "," + offsetY.ToString("0"));
            }

            /// <summary>把方块拖到光标处（鼠标移动与自动化测试共用同一套逻辑）。</summary>
            internal void DragTo(Point cursor)
            {
                Point center = WindowsCenter;
                double rawLeft = cursor.X - grab.X;
                double rawTop = cursor.Y - grab.Y;
                double wantX = rawLeft - (center.X - BoxW / 2);
                double wantY = rawTop - (center.Y - BoxH / 2);

                double snapX, snapY;
                NearestOffset(wantX, wantY, out snapX, out snapY);
                double dx = wantX - snapX;
                double dy = wantY - snapY;
                if (Math.Sqrt(dx * dx + dy * dy) < Magnet)
                {
                    wantX = snapX;
                    wantY = snapY;
                    grab = new Point(cursor.X - (center.X - BoxW / 2 + wantX),
                        cursor.Y - (center.Y - BoxH / 2 + wantY));
                }

                offsetX = wantX;
                offsetY = wantY;
                Arrange();

                if (!dragLogged)
                {
                    dragLogged = true;
                    Log.Write("arranger drag move to offset=" + offsetX.ToString("0") + ","
                        + offsetY.ToString("0"));
                }
            }

            /// <summary>松手：按偏移的主方向决定从哪条边切屏，斜角也算（左上/右下等）。</summary>
            private void CommitDrop()
            {
                double stepX = BoxW + Gap;
                double stepY = BoxH + Gap;

                // 斜放在四角时，主方向取偏移更大的那个轴，保证切屏行为确定
                if (Math.Abs(offsetX) / stepX >= Math.Abs(offsetY) / stepY)
                    edge = offsetX < 0 ? "left" : "right";
                else
                    edge = offsetY < 0 ? "top" : "bottom";

                Arrange();
                Notify();
            }

            /// <summary>最近的整齐位置（左中右 × 上中下，共 8 个，去掉重叠的中心）。</summary>
            private void NearestOffset(double x, double y, out double snapX, out double snapY)
            {
                double stepX = BoxW + Gap;
                double stepY = BoxH + Gap;
                string[] grid = new string[] { "-1", "0", "1" };
                double best = double.MaxValue;
                double bx = x;
                double by = y;
                foreach (string sx in grid)
                {
                    foreach (string sy in grid)
                    {
                        int ix = int.Parse(sx);
                        int iy = int.Parse(sy);
                        if (ix == 0 && iy == 0)
                            continue;   // 完全重叠的位置没有意义
                        double cx = ix * stepX;
                        double cy = iy * stepY;
                        double dx = x - cx;
                        double dy = y - cy;
                        double distance = Math.Sqrt(dx * dx + dy * dy);
                        if (distance < best)
                        {
                            best = distance;
                            bx = cx;
                            by = cy;
                        }
                    }
                }
                snapX = bx;
                snapY = by;
            }

            private void Arrange()
            {
                double width = ViewWidth;
                Point center = WindowsCenter;

                // Windows 固定在画布中心，Ubuntu 按偏移摆放（斜角也能放）
                double winLeft = center.X - BoxW / 2;
                double winTop = center.Y - BoxH / 2;
                double ubuLeft = winLeft + offsetX;
                double ubuTop = winTop + offsetY;

                // 夹在画布内，窗口变窄时也不会跑到看不见的地方
                double minLeft = Math.Min(winLeft, ubuLeft);
                double maxRight = Math.Max(winLeft, ubuLeft) + BoxW;
                double minTop = Math.Min(winTop, ubuTop);
                double maxBottom = Math.Max(winTop, ubuTop) + BoxH;
                double shiftX = minLeft < Padding ? Padding - minLeft
                    : (maxRight > width - Padding ? width - Padding - maxRight : 0);
                double shiftY = minTop < Padding ? Padding - minTop
                    : (maxBottom > CanvasH - Padding ? CanvasH - Padding - maxBottom : 0);

                SetLeft(windows, winLeft + shiftX);
                SetTop(windows, winTop + shiftY);
                SetLeft(ubuntu, ubuLeft + shiftX);
                SetTop(ubuntu, ubuTop + shiftY);
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
