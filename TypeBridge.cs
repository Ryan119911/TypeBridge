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

[assembly: AssemblyTitle("TypeBridge")]
[assembly: AssemblyDescription("Windowless Windows to Ubuntu keyboard and mouse bridge")]
[assembly: AssemblyProduct("TypeBridge")]

namespace TypeBridgeApp
{
    /// <summary>屏幕摆放的公共几何：设置页派生方向与实际切屏必须用同一套公式。</summary>
    internal static class LayoutMath
    {
        internal const double BoxW = 132;
        internal const double BoxH = 82;
        internal const double Gap = 18;

        internal static string EdgeFromOffset(double offsetX, double offsetY)
        {
            double stepX = BoxW + Gap;
            double stepY = BoxH + Gap;
            if (Math.Abs(offsetX) / stepX >= Math.Abs(offsetY) / stepY)
                return offsetX < 0 ? "left" : "right";
            return offsetY < 0 ? "top" : "bottom";
        }

        internal static bool TryParseOffset(string text, out double dx, out double dy)
        {
            dx = 0;
            dy = 0;
            string[] xy = (text ?? string.Empty).Split(',');
            if (xy.Length != 2)
                return false;
            return double.TryParse(xy[0].Trim(), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out dx)
                && double.TryParse(xy[1].Trim(), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out dy);
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            try
            {
                string[] commandLine = Environment.GetCommandLineArgs();
                if (commandLine.Length >= 2 && commandLine[1] == "--uninstall")
                {
                    RunUninstaller();
                    return;
                }
                if (commandLine.Length >= 3 && commandLine[1] == "--send")
                {
                    // 命令行发文件：给脚本/自动化留的口子，也方便不碰托盘直接传
                    RunCliSend(commandLine);
                    return;
                }

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
                    {
                        if (Array.IndexOf(commandLine, "--wait-for-exit") < 0) return;
                        try
                        {
                            if (!mutex.WaitOne(10000)) return;
                        }
                        catch (System.Threading.AbandonedMutexException)
                        {
                            // The previous process exited while owning the mutex.
                            // WaitOne has granted this thread ownership.
                        }
                    }

                    // 上次可能异常退出，系统光标还停在空白图上，启动先恢复
                    GlobalMouseHook.RestoreSystemCursors();
                    // 退出（含 Ctrl+C / 关闭进程）时再兜一次
                    AppDomain.CurrentDomain.ProcessExit += delegate
                    {
                        GlobalMouseHook.RestoreSystemCursors();
                    };
                    // 鼠标钩子拿到的是物理像素坐标，声明 DPI 感知后才能和光标坐标对齐
                    try { SetProcessDPIAware(); } catch { }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (BridgeApplicationContext context = new BridgeApplicationContext())
                    {
                        try { Application.Run(context); }
                        finally { context.ExitThread(); }
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

        /// <summary>把 ini 里的多地址拆成候选列表（逗号 / 分号 / 全角 / 空白）。</summary>
        internal static string[] ParseHostList(string value)
        {
            List<string> list = new List<string>();
            foreach (string part in (value ?? string.Empty).Split(
                new char[] { ',', ';', '\uFF0C', '\uFF1B', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string host = part.Trim();
                if (host.Length > 0 && !list.Contains(host))
                    list.Add(host);
            }
            if (list.Count == 0)
                list.Add("127.0.0.1");
            return list.ToArray();
        }

        /// <summary>
        /// --uninstall：卸载入口。会先清快捷方式、开机自启和卸载信息，再删安装目录。
        /// 程序正在安装目录里运行时交给临时脚本延时删除，所以这里不要弹"卸载完成"再等用户点确定。
        /// </summary>
        private static void RunUninstaller()
        {
            try { SetProcessDPIAware(); } catch { }
            string exe = WindowsInstall.CurrentExecutable;
            InstallLayout layout = WindowsInstall.LayoutFor(exe);
            if (!WindowsInstall.IsInstalledAt(exe, layout))
            {
                MessageBox.Show(
                    "没有找到通过安装程序安装的 TypeBridge。\r\n\r\n"
                        + "如果只是免安装使用，直接删除程序文件即可。",
                    "卸载 TypeBridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(
                    "确定要卸载 TypeBridge 吗？\r\n\r\n"
                        + "会删除开始菜单和桌面快捷方式、开机启动项，以及安装目录中的程序文件。",
                    "卸载 TypeBridge",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            if (WindowsInstall.IsAnotherInstanceRunning())
            {
                if (MessageBox.Show(
                        "卸载前需要关闭正在运行的 TypeBridge。\r\n\r\n现在关闭并继续卸载吗？",
                        "卸载 TypeBridge",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
                WindowsInstall.StopOtherInstances();
            }

            string error;
            if (!WindowsInstall.Uninstall(layout, exe, out error))
            {
                MessageBox.Show("卸载失败：" + error, "卸载 TypeBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 命令行发文件：TypeBridge.exe --send &lt;文件&gt; [更多文件…]
        /// 给脚本留的口子，也方便不点托盘直接传（不影响正在运行的托盘实例）。
        /// </summary>
        private static void RunCliSend(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string[] cliHosts = new string[] { "127.0.0.1" };
            int port = 4322;
            try
            {
                string ini = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "typebridge.ini");
                if (System.IO.File.Exists(ini))
                {
                    foreach (string raw in System.IO.File.ReadAllLines(ini))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0)
                            continue;
                        string key = raw.Substring(0, eq).Trim().ToLowerInvariant();
                        string value = raw.Substring(eq + 1).Trim();
                        if (key == "ip" && value.Length > 0)
                            cliHosts = Program.ParseHostList(value);
                        else if (key == "file_port")
                        {
                            // TryParse 失败会把 out 置 0，必须先落在临时变量上校验
                            int parsedPort;
                            if (int.TryParse(value, out parsedPort) && parsedPort > 0 && parsedPort <= 65535)
                                port = parsedPort;
                        }
                    }
                }
            }
            catch { }

            string[] files = new string[args.Length - 2];
            Array.Copy(args, 2, files, 0, files.Length);

            int ok = 0;
            string lastDestination = null;
            string firstProblem = null;
            TransferProgressForm progress = new TransferProgressForm();

            progress.Shown += delegate
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    foreach (string file in files)
                    {
                        if (progress.CancelRequested)
                            break;
                        string error;
                        string destination;
                        bool uploaded = false;
                        string lastError = null;
                        // 配置里可以写多个地址，CLI 也要像 GUI 一样逐个尝试，
                        // 不能把含逗号的整串 ip 直接交给 ConnectAsync。
                        foreach (string host in cliHosts)
                        {
                            if (FileSender.Upload(host, port, file, progress, out error, out destination))
                            {
                                uploaded = true;
                                ok++;
                                if (destination != null)
                                    lastDestination = destination;
                                break;
                            }
                            lastError = error;
                        }
                        if (!uploaded && firstProblem == null)
                        {
                            firstProblem = System.IO.Path.GetFileName(file) + "：" + (lastError ?? "发送失败");
                        }
                    }

                    string message = "已发送 " + ok + " / " + files.Length + " 个文件到 Ubuntu";
                    if (ok > 0 && lastDestination != null)
                        message += " → " + lastDestination;
                    if (firstProblem != null)
                        message += "（" + firstProblem + "）";
                    Log.Write("cli send: " + message);

                    progress.Finish(message, false);
                    System.Threading.Thread.Sleep(1500);
                    try { progress.BeginInvoke(new Action(delegate { progress.Close(); })); }
                    catch { }
                });
            };

            progress.BeginTransfer();
            Application.Run(progress);
            Environment.ExitCode = (ok == files.Length && firstProblem == null) ? 0 : 1;
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
    }

    /// <summary>
    /// 轻量诊断日志：记录模式切换、热键命中、连接状态。
    /// 出问题时直接看 typebridge.log 就能定位，不用再靠猜。
    /// 超过 256KB 自动轮换成 .1 一份，不会无限增长。
    /// </summary>
}
