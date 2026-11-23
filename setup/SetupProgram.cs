using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle("TypeBridge 安装程序")]
[assembly: AssemblyDescription("安装 TypeBridge 到本机并注册开始菜单快捷方式")]
[assembly: AssemblyProduct("TypeBridge")]

namespace TypeBridgeApp
{
    /// <summary>
    /// TypeBridgeSetup.exe：内嵌 TypeBridge.exe 的独立安装程序。
    /// 开始菜单快捷方式固定创建，桌面快捷方式和开机自启由用户在界面上勾选。
    /// </summary>
    internal static class SetupProgram
    {
        private const string PayloadName = "TypeBridge.exe";

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            string payloadDirectory = null;
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { SetProcessDPIAware(); } catch { }

                string payload;
                try
                {
                    payload = ExtractPayload(out payloadDirectory);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "安装包损坏，无法读取 " + PayloadName + "。\r\n\r\n" + ex.Message,
                        "TypeBridge 安装程序",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                EnsureWpfApplication();

                string installedDir = WindowsInstall.ReadInstalledDirectory();
                bool installed = !string.IsNullOrEmpty(installedDir);
                string suggested = installed ? installedDir : WindowsInstall.DefaultInstallDirectory;

                // 升级时沿用现有选项，别把用户关掉的桌面图标/自启又打开
                bool autoStart = true;
                bool desktop = true;
                if (installed)
                {
                    InstallLayout existing = WindowsInstall.LayoutFor(
                        Path.Combine(installedDir, WindowsInstall.ExecutableName));
                    IntegrationState state = WindowsInstall.ReadState(
                        Path.Combine(installedDir, WindowsInstall.ExecutableName), existing);
                    autoStart = state.AutoStart;
                    desktop = state.DesktopShortcut;
                }

                WpfInstallWindow window = new WpfInstallWindow(payload, suggested, autoStart, desktop);
                if (window.ShowDialog() != true)
                    return;

                string installedExe = window.InstalledExecutable;
                string folder = Path.GetDirectoryName(installedExe);
                CopyBundledConfiguration(folder);

                if (WindowsInstall.IsAnotherInstanceRunning())
                {
                    MessageBox.Show(
                        "TypeBridge 已安装到：\r\n" + folder
                            + "\r\n\r\n当前还有一个 TypeBridge 正在运行。"
                            + "请先从托盘退出它，再从开始菜单启动新安装的版本。",
                        "安装完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                if (MessageBox.Show(
                        "TypeBridge 已安装到：\r\n" + folder + "\r\n\r\n现在就启动吗？",
                        "安装完成",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    string error;
                    if (!WindowsInstall.StartInstalled(installedExe, false, out error))
                    {
                        MessageBox.Show(
                            "启动失败：" + error,
                            "TypeBridge 安装程序",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "安装程序启动失败：\r\n" + ex.Message,
                    "TypeBridge 安装程序",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                TryDeleteDirectory(payloadDirectory);
            }
        }

        /// <summary>把内嵌的 TypeBridge.exe 释放到临时目录，交给安装窗口复制到目标位置。</summary>
        private static string ExtractPayload(out string directory)
        {
            directory = null;
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(PayloadName))
            {
                if (stream == null)
                    throw new InvalidOperationException("安装包里没有找到内嵌的 " + PayloadName);

                directory = Path.Combine(Path.GetTempPath(),
                    "TypeBridgeSetup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, PayloadName);
                using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    stream.CopyTo(file);

                if (new FileInfo(path).Length == 0)
                    throw new InvalidDataException("内嵌的 " + PayloadName + " 是空文件");
                return path;
            }
        }

        /// <summary>
        /// 安装包旁边如果放了 typebridge.ini，首次安装时带过去，便于批量部署；
        /// 已有配置（升级/重装）绝不覆盖。
        /// </summary>
        private static void CopyBundledConfiguration(string installDir)
        {
            try
            {
                string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "typebridge.ini");
                string target = Path.Combine(installDir, "typebridge.ini");
                if (File.Exists(source) && !File.Exists(target))
                    File.Copy(source, target);
            }
            catch
            {
                // 配置复制失败不影响安装，程序会在首次启动时让用户填
            }
        }

        private static void EnsureWpfApplication()
        {
            if (System.Windows.Application.Current == null)
            {
                System.Windows.Application app = new System.Windows.Application();
                app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            }
        }

        private static void TryDeleteDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch { }
        }
    }
}
