using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TypeBridgeApp
{
    /// <summary>
    /// 安装窗口：选择安装目录，决定是否开机自启、是否创建桌面快捷方式。
    /// 开始菜单快捷方式在安装时固定创建，所以不提供勾选。
    /// </summary>
    internal sealed class WpfInstallWindow : Window
    {
        private readonly TextBox dirBox = new TextBox();
        private readonly WpfUi.Switch autoStartSwitch = new WpfUi.Switch();
        private readonly WpfUi.Switch desktopSwitch = new WpfUi.Switch();
        private readonly TextBlock errorText = new TextBlock();
        private readonly string sourceExe;
        private Button installButton;
        private Button cancelButton;
        private bool installing;
        private string installedExe;

        internal string InstalledExecutable { get { return installedExe; } }

        internal WpfInstallWindow(string sourceExePath, string suggestedDir, bool autoStart, bool desktop)
        {
            sourceExe = sourceExePath;

            Title = "安装 TypeBridge";
            WindowStyle = WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Background = new SolidColorBrush(Color.FromRgb(245, 248, 254));
            Width = 580;
            Height = Math.Min(600, SystemParameters.WorkArea.Height - 60);
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            autoStartSwitch.IsOn = autoStart;
            desktopSwitch.IsOn = desktop;

            Content = BuildContent();
            dirBox.Text = suggestedDir;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (installing)
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }

        private UIElement BuildContent()
        {
            Border shell = new Border
            {
                Margin = new Thickness(0),
                Background = new LinearGradientBrush(
                    Color.FromRgb(255, 255, 255), Color.FromRgb(240, 245, 253),
                    new Point(0, 0), new Point(0.3, 1))
            };

            StackPanel stack = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };

            stack.Children.Add(new TextBlock
            {
                Text = "把 TypeBridge 安装到本机",
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(WpfUi.Ink)
            });
            stack.Children.Add(new TextBlock
            {
                Text = "安装后会出现在开始菜单和应用列表里，也可以按需要设为开机启动。",
                FontSize = 12.5,
                Foreground = new SolidColorBrush(WpfUi.Sub),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 14)
            });

            StackPanel locationBody = new StackPanel();
            UIElement locationCard = WpfUi.Card("安装位置", locationBody);
            locationBody.Children.Add(WpfUi.Label("程序会复制到这个文件夹"));
            Grid pathRow = new Grid();
            pathRow.ColumnDefinitions.Add(new ColumnDefinition());
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pathRow.Children.Add(WpfUi.Field(dirBox));
            Button browse = WpfUi.SecondaryButton("浏览");
            browse.Margin = new Thickness(8, 0, 0, 0);
            browse.Click += delegate
            {
                using (System.Windows.Forms.FolderBrowserDialog dialog =
                    new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = "选择 TypeBridge 的安装文件夹";
                    if (Directory.Exists(dirBox.Text))
                        dialog.SelectedPath = dirBox.Text;
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        dirBox.Text = dialog.SelectedPath;
                }
            };
            Grid.SetColumn(browse, 1);
            pathRow.Children.Add(browse);
            locationBody.Children.Add(pathRow);
            locationBody.Children.Add(WpfUi.Note(
                "只装给当前用户，不需要管理员权限。默认放在本机应用目录，卸载时会一并删除。"));
            stack.Children.Add(locationCard);

            StackPanel optionBody = new StackPanel();
            UIElement optionCard = WpfUi.Card("安装选项", optionBody);
            optionBody.Children.Add(WpfUi.ToggleRow(autoStartSwitch,
                "开机自动启动", "登录 Windows 后自动在托盘里运行，不用每次手动打开"));
            optionBody.Children.Add(WpfUi.ToggleRow(desktopSwitch,
                "创建桌面快捷方式", "在桌面放一个 TypeBridge 图标"));
            TextBlock startMenuNote = WpfUi.Note(
                "开始菜单快捷方式会在安装时自动添加，之后在开始菜单搜索 TypeBridge 就能打开。");
            startMenuNote.Margin = new Thickness(0, 16, 0, 0);
            optionBody.Children.Add(startMenuNote);
            stack.Children.Add(optionCard);

            errorText.Foreground = new SolidColorBrush(Color.FromRgb(198, 52, 52));
            errorText.FontSize = 12;
            errorText.Margin = new Thickness(2, 0, 0, 0);
            errorText.TextWrapping = TextWrapping.Wrap;
            errorText.Visibility = Visibility.Collapsed;
            stack.Children.Add(errorText);

            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            });

            UIElement footer = BuildFooter();
            Grid.SetRow(footer, 1);
            layout.Children.Add(footer);
            shell.Child = layout;
            return shell;
        }

        private UIElement BuildFooter()
        {
            Grid footer = new Grid { Margin = new Thickness(22, 8, 22, 16) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            cancelButton = WpfUi.SecondaryButton("取消");
            cancelButton.Width = 96;
            cancelButton.HorizontalAlignment = HorizontalAlignment.Right;
            cancelButton.Click += delegate { Close(); };

            installButton = WpfUi.PrimaryButton("安装");
            installButton.Width = 110;
            installButton.Margin = new Thickness(10, 0, 0, 0);
            installButton.IsDefault = true;
            installButton.Click += delegate { StartInstall(); };

            cancelButton.IsCancel = true;

            Grid.SetColumn(cancelButton, 1);
            Grid.SetColumn(installButton, 2);
            footer.Children.Add(cancelButton);
            footer.Children.Add(installButton);
            return footer;
        }

        private void StartInstall()
        {
            if (installing)
                return;

            string dir = (dirBox.Text ?? string.Empty).Trim();
            if (dir.Length == 0)
            {
                Fail("请选择安装位置");
                return;
            }
            try
            {
                dir = Path.GetFullPath(dir);
            }
            catch
            {
                Fail("安装位置不是合法路径");
                return;
            }
            if (WindowsInstall.PathsEqual(Path.GetPathRoot(dir), dir))
            {
                Fail("不能安装到磁盘根目录，请选择子文件夹");
                return;
            }
            dirBox.Text = dir;

            installing = true;
            installButton.IsEnabled = false;
            installButton.Content = "正在安装…";
            cancelButton.IsEnabled = false;
            errorText.Visibility = Visibility.Collapsed;

            string source = sourceExe;
            bool autoStart = autoStartSwitch.IsOn;
            bool desktop = desktopSwitch.IsOn;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                InstallLayout layout = WindowsInstall.DefaultLayout(dir);
                string installedPath = null;
                string error = null;
                bool ok = false;
                try
                {
                    ok = WindowsInstall.Install(source, layout, autoStart, desktop,
                        out installedPath, out error);
                }
                catch (Exception ex)
                {
                    ok = false;
                    error = ex.Message;
                }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    installing = false;
                    installButton.IsEnabled = true;
                    installButton.Content = "安装";
                    cancelButton.IsEnabled = true;
                    if (!ok)
                    {
                        Fail(error);
                        return;
                    }
                    installedExe = installedPath;
                    DialogResult = true;
                    Close();
                }));
            });
        }

        private void Fail(string message)
        {
            errorText.Text = string.IsNullOrEmpty(message) ? "安装失败" : message;
            errorText.Visibility = Visibility.Visible;
        }
    }
}
