using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TypeBridgeApp
{
    /// <summary>
    /// 本机安装布局。路径全部显式传入，测试可以指到临时目录，不会碰到真实的开始菜单。
    /// </summary>
    internal sealed class InstallLayout
    {
        internal string InstallDir;
        internal string StartMenuDir;
        internal string DesktopDir;
        internal string RunKeyPath;
        internal string UninstallKeyPath;
        internal string ValueName;

        internal string ExecutablePath
        {
            get { return Path.Combine(InstallDir, WindowsInstall.ExecutableName); }
        }

        internal string StartMenuShortcut
        {
            get { return Path.Combine(StartMenuDir, WindowsInstall.AppName + ".lnk"); }
        }

        internal string DesktopShortcut
        {
            get { return Path.Combine(DesktopDir, WindowsInstall.AppName + ".lnk"); }
        }
    }

    /// <summary>设置页要显示的当前集成状态。</summary>
    internal sealed class IntegrationState
    {
        internal bool AutoStart;
        internal bool StartMenuShortcut;
        internal bool DesktopShortcut;
        internal bool Installed;
        internal string InstallDirectory;
        internal string ExecutablePath;
    }

    /// <summary>
    /// 安装到本机、开始菜单 / 桌面快捷方式、开机自启、卸载信息。
    /// 全部写在当前用户名下（%LOCALAPPDATA% + HKCU），不需要管理员权限。
    /// </summary>
    internal static class WindowsInstall
    {
        internal const string AppName = "TypeBridge";
        internal const string ExecutableName = "TypeBridge.exe";
        /// <summary>开机自启时带上这个参数：程序照常连接，但不弹"已启动"提示。</summary>
        internal const string AutoStartArgument = "--autostart";
        internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TypeBridge";

        internal static string CurrentExecutable
        {
            get
            {
                try
                {
                    string location = Assembly.GetEntryAssembly().Location;
                    if (!string.IsNullOrEmpty(location))
                        return location;
                }
                catch { }
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExecutableName);
            }
        }

        internal static string DefaultInstallDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", AppName);
            }
        }

        internal static string StartMenuDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs");
            }
        }

        internal static string DesktopDirectory
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        }

        internal static InstallLayout DefaultLayout(string installDir)
        {
            InstallLayout layout = new InstallLayout();
            layout.InstallDir = string.IsNullOrEmpty(installDir) ? DefaultInstallDirectory : installDir;
            layout.StartMenuDir = StartMenuDirectory;
            layout.DesktopDir = DesktopDirectory;
            layout.RunKeyPath = RunKeyPath;
            layout.UninstallKeyPath = UninstallKeyPath;
            layout.ValueName = AppName;
            return layout;
        }

        /// <summary>以"当前程序所在目录"为安装位置，用来读写它自己的快捷方式/自启状态。</summary>
        internal static InstallLayout LayoutFor(string exePath)
        {
            string dir = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
            return DefaultLayout(dir);
        }

        // ---------- 路径与命令行 ----------

        internal static string Quote(string path)
        {
            return "\"" + path + "\"";
        }

        internal static string AutoStartCommand(string exePath)
        {
            return Quote(exePath) + " " + AutoStartArgument;
        }

        /// <summary>从 Run 值里取出可执行文件路径（我们写的是带引号的完整路径）。</summary>
        internal static string ExecutableFromCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
                return string.Empty;
            command = command.Trim();
            if (command.Length == 0)
                return string.Empty;
            if (command[0] == '"')
            {
                int end = command.IndexOf('"', 1);
                if (end > 1)
                    return command.Substring(1, end - 1);
            }
            int space = command.IndexOf(' ');
            return space > 0 ? command.Substring(0, space) : command;
        }

        internal static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd('\\'),
                    Path.GetFullPath(right).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ---------- 开机自启 ----------

        internal static string ReadAutoStart(string runKeyPath, string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(runKeyPath, false))
                {
                    if (key == null)
                        return null;
                    return key.GetValue(valueName) as string;
                }
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsAutoStartFor(string exePath, string runKeyPath, string valueName)
        {
            string command = ReadAutoStart(runKeyPath, valueName);
            return command != null && PathsEqual(ExecutableFromCommand(command), exePath);
        }

        internal static bool SetAutoStart(string exePath, bool enabled, string runKeyPath,
            string valueName, out string error)
        {
            error = null;
            try
            {
                if (enabled)
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(runKeyPath))
                    {
                        if (key == null)
                        {
                            error = "无法打开注册表启动项";
                            return false;
                        }
                        key.SetValue(valueName, AutoStartCommand(exePath), RegistryValueKind.String);
                    }
                }
                else
                {
                    // 关闭时不要凭空创建 Run 键
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(runKeyPath, true))
                    {
                        if (key != null)
                            key.DeleteValue(valueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---------- 快捷方式 ----------

        internal static bool CreateShortcut(string shortcutPath, string targetPath, string arguments,
            string description, out string error)
        {
            error = null;
            object comObject = null;
            try
            {
                string folder = Path.GetDirectoryName(shortcutPath);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);

                comObject = new ShellLink();
                IShellLinkW link = (IShellLinkW)comObject;
                link.SetPath(targetPath);
                if (!string.IsNullOrEmpty(arguments))
                    link.SetArguments(arguments);
                link.SetWorkingDirectory(Path.GetDirectoryName(targetPath));
                link.SetDescription(description);
                link.SetIconLocation(targetPath, 0);
                ((IPersistFile)comObject).Save(shortcutPath, true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (comObject != null)
                {
                    try { Marshal.FinalReleaseComObject(comObject); }
                    catch { }
                }
            }
        }

        internal static bool DeleteShortcut(string shortcutPath, out string error)
        {
            error = null;
            try
            {
                if (File.Exists(shortcutPath))
                    File.Delete(shortcutPath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>读取 .lnk 指向的目标，供测试和"快捷方式是否还指着我"的判断使用。</summary>
        internal static string ReadShortcutTarget(string shortcutPath)
        {
            if (!File.Exists(shortcutPath))
                return null;
            object comObject = null;
            try
            {
                comObject = new ShellLink();
                ((IPersistFile)comObject).Load(shortcutPath, 0);
                StringBuilder buffer = new StringBuilder(1024);
                ((IShellLinkW)comObject).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
                return buffer.ToString();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (comObject != null)
                {
                    try { Marshal.FinalReleaseComObject(comObject); }
                    catch { }
                }
            }
        }

        // ---------- 状态 ----------

        internal static IntegrationState ReadState(string exePath, InstallLayout layout)
        {
            IntegrationState state = new IntegrationState();
            state.ExecutablePath = exePath;
            state.InstallDirectory = layout.InstallDir;
            state.AutoStart = IsAutoStartFor(exePath, layout.RunKeyPath, layout.ValueName);
            state.StartMenuShortcut = File.Exists(layout.StartMenuShortcut);
            state.DesktopShortcut = File.Exists(layout.DesktopShortcut);
            state.Installed = IsInstalledAt(exePath, layout);
            return state;
        }

        /// <summary>读卸载信息里的安装目录，供安装程序判断"这是升级还是首次安装"。</summary>
        internal static string ReadInstalledDirectory()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, false))
                {
                    if (key == null)
                        return null;
                    string location = key.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(location))
                        return null;
                    if (!File.Exists(Path.Combine(location, ExecutableName)))
                        return null;
                    return location;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>卸载信息里的安装目录和当前 exe 所在目录一致，才算"这个副本是安装的"。</summary>
        internal static bool IsInstalledAt(string exePath, InstallLayout layout)
        {
            if (!PathsEqual(Path.GetDirectoryName(exePath), layout.InstallDir))
                return false;
            if (!File.Exists(layout.ExecutablePath))
                return false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(layout.UninstallKeyPath, false))
                {
                    if (key == null)
                        return false;
                    string location = key.GetValue("InstallLocation") as string;
                    return PathsEqual(location, Path.GetDirectoryName(exePath));
                }
            }
            catch
            {
                return false;
            }
        }

        // ---------- 设置页的三项开关 ----------

        internal static bool ApplySettings(string exePath, bool autoStart, bool startMenuShortcut,
            bool desktopShortcut, InstallLayout layout, out string error)
        {
            if (!SetAutoStart(exePath, autoStart, layout.RunKeyPath, layout.ValueName, out error))
                return false;

            if (startMenuShortcut)
            {
                if (!CreateShortcut(layout.StartMenuShortcut, exePath, string.Empty,
                        ShortcutDescription, out error))
                    return false;
            }
            else if (!DeleteShortcut(layout.StartMenuShortcut, out error))
            {
                return false;
            }

            if (desktopShortcut)
            {
                if (!CreateShortcut(layout.DesktopShortcut, exePath, string.Empty,
                        ShortcutDescription, out error))
                    return false;
            }
            else if (!DeleteShortcut(layout.DesktopShortcut, out error))
            {
                return false;
            }
            return true;
        }

        private const string ShortcutDescription = "Windows ⇄ Ubuntu 键鼠与文件共享";

        // ---------- 安装 ----------

        /// <summary>
        /// 把当前 exe 复制到安装目录，创建开始菜单快捷方式（固定创建）、可选的桌面快捷方式和开机自启。
        /// 已存在的 typebridge.ini 不会被覆盖，重装/升级时用户配置保持不变。
        /// </summary>
        internal static bool Install(string sourceExe, InstallLayout layout, bool autoStart,
            bool desktopShortcut, out string installedExe, out string error)
        {
            installedExe = layout.ExecutablePath;
            error = null;

            if (!File.Exists(sourceExe))
            {
                error = "找不到要安装的程序文件：" + sourceExe;
                return false;
            }

            string fullDir;
            try
            {
                fullDir = Path.GetFullPath(layout.InstallDir);
                if (PathsEqual(Path.GetPathRoot(fullDir), fullDir))
                {
                    error = "不能安装到磁盘根目录，请选择子文件夹";
                    return false;
                }
                Directory.CreateDirectory(fullDir);
                layout.InstallDir = fullDir;
            }
            catch (Exception ex)
            {
                error = "无法创建安装目录：" + ex.Message;
                return false;
            }

            if (!CopyExecutable(sourceExe, layout.ExecutablePath, out error))
                return false;
            CopyConfigurationIfMissing(Path.GetDirectoryName(sourceExe), layout.InstallDir);

            // 开始菜单是安装时自动注册的，不提供开关
            if (!CreateShortcut(layout.StartMenuShortcut, layout.ExecutablePath, string.Empty,
                    ShortcutDescription, out error))
                return false;

            if (desktopShortcut)
            {
                if (!CreateShortcut(layout.DesktopShortcut, layout.ExecutablePath, string.Empty,
                        ShortcutDescription, out error))
                    return false;
            }
            else
            {
                // 明确不勾选时，连旧安装留下的桌面图标一起清掉
                if (!DeleteShortcut(layout.DesktopShortcut, out error))
                    return false;
            }

            if (!SetAutoStart(layout.ExecutablePath, autoStart, layout.RunKeyPath, layout.ValueName, out error))
                return false;

            return WriteUninstallEntry(layout, out error);
        }

        /// <summary>
        /// 复制 exe。目标正在运行（文件被锁）时先改名再放新文件——Windows 允许重命名运行中的 exe，
        /// 这样"在已安装版本上重新安装"不需要用户先退出。
        /// </summary>
        private static bool CopyExecutable(string source, string target, out string error)
        {
            error = null;
            string staging = target + ".new-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
            string backup = null;
            try
            {
                string folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);
                File.Copy(source, staging, true);

                if (File.Exists(target))
                {
                    try
                    {
                        File.Delete(target);
                    }
                    catch
                    {
                        // 运行中的 exe 删不掉，但可以改名；旧文件留着，下次启动前用户可手动清理
                        backup = target + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                        File.Move(target, backup);
                    }
                }
                try
                {
                    File.Move(staging, target);
                }
                catch
                {
                    // 新文件放不进去就把旧文件恢复回原路径，别让程序凭空消失
                    if (backup != null && !File.Exists(target))
                    {
                        try { File.Move(backup, target); }
                        catch { }
                    }
                    throw;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "复制程序文件失败：" + ex.Message;
                try { if (File.Exists(staging)) File.Delete(staging); }
                catch { }
                return false;
            }
        }

        private static void CopyConfigurationIfMissing(string sourceDir, string installDir)
        {
            try
            {
                if (string.IsNullOrEmpty(sourceDir))
                    return;
                string source = Path.Combine(sourceDir, "typebridge.ini");
                string target = Path.Combine(installDir, "typebridge.ini");
                if (File.Exists(source) && !PathsEqual(source, target) && !File.Exists(target))
                    File.Copy(source, target);
            }
            catch
            {
                // 配置复制失败不影响安装：程序会使用默认值
            }
        }

        private static bool WriteUninstallEntry(InstallLayout layout, out string error)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(layout.UninstallKeyPath))
                {
                    if (key == null)
                    {
                        error = "无法写入卸载信息";
                        return false;
                    }
                    Version version = Assembly.GetExecutingAssembly().GetName().Version;
                    string versionText = version == null ? "1.0.0"
                        : version.Major + "." + version.Minor + "." + Math.Max(0, version.Build);
                    key.SetValue("DisplayName", AppName);
                    key.SetValue("DisplayVersion", versionText);
                    key.SetValue("Publisher", AppName);
                    key.SetValue("DisplayIcon", layout.ExecutablePath + ",0");
                    key.SetValue("InstallLocation", layout.InstallDir);
                    key.SetValue("UninstallString", Quote(layout.ExecutablePath) + " --uninstall");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    key.SetValue("EstimatedSize", EstimateSizeKb(layout.InstallDir), RegistryValueKind.DWord);
                }
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static int EstimateSizeKb(string folder)
        {
            long bytes = 0;
            try
            {
                foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    try { bytes += new FileInfo(file).Length; }
                    catch { }
                }
            }
            catch { }
            long kb = (bytes + 1023) / 1024;
            return kb > int.MaxValue ? int.MaxValue : (int)Math.Max(1, kb);
        }

        // ---------- 卸载 ----------

        internal static bool RemoveIntegration(InstallLayout layout, out string error)
        {
            bool ok = true;
            string firstError = null;

            string stepError;
            // 只删指向这个副本的启动项：机器上如果还留着另一份 TypeBridge，别把它的自启一起删了
            string command = ReadAutoStart(layout.RunKeyPath, layout.ValueName);
            if (command != null && PathsEqual(ExecutableFromCommand(command), layout.ExecutablePath)
                && !SetAutoStart(layout.ExecutablePath, false, layout.RunKeyPath,
                    layout.ValueName, out stepError))
            {
                ok = false;
                firstError = stepError;
            }
            if (!DeleteShortcut(layout.StartMenuShortcut, out stepError))
            {
                ok = false;
                if (firstError == null) firstError = stepError;
            }
            if (!DeleteShortcut(layout.DesktopShortcut, out stepError))
            {
                ok = false;
                if (firstError == null) firstError = stepError;
            }
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(layout.UninstallKeyPath, false);
            }
            catch (Exception ex)
            {
                ok = false;
                if (firstError == null) firstError = ex.Message;
            }

            error = ok ? null : firstError;
            return ok;
        }

        /// <summary>卸载：先清快捷方式/自启/卸载信息，再删程序目录。程序自己正在该目录里时交给临时脚本延时删除。</summary>
        internal static bool Uninstall(InstallLayout layout, string runningExe, out string error)
        {
            if (!RemoveIntegration(layout, out error))
                return false;

            try
            {
                if (!Directory.Exists(layout.InstallDir))
                    return true;

                bool runningFromInstallDir = PathsEqual(
                    Path.GetDirectoryName(runningExe), layout.InstallDir);
                if (!runningFromInstallDir)
                {
                    DeleteInstalledFiles(layout.InstallDir);
                    TryRemoveEmptyDirectory(layout.InstallDir);
                    return true;
                }

                if (!ScheduleDirectoryRemoval(layout.InstallDir, out error))
                    return false;
                return true;
            }
            catch (Exception ex)
            {
                error = "删除程序文件失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 只删安装器自己放进去的文件。安装目录是用户选的，里面可能有别的文件，
        /// 不能整个 rd /s /q 掉（比如有人把程序装进了自己的项目目录）。
        /// </summary>
        private static void DeleteInstalledFiles(string folder)
        {
            string[] exact =
            {
                ExecutableName,
                "typebridge.ini",
                "typebridge.log",
                "typebridge.log.1",
                "typebridge-error.log"
            };
            foreach (string name in exact)
                TryDeleteFile(Path.Combine(folder, name));

            // 升级时留下的旧 exe 备份 / 临时文件也一并清掉
            TryDeleteFiles(folder, ExecutableName + ".old-*");
            TryDeleteFiles(folder, ExecutableName + ".new-*");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private static void TryDeleteFiles(string folder, string pattern)
        {
            try
            {
                foreach (string file in Directory.GetFiles(folder, pattern))
                    TryDeleteFile(file);
            }
            catch { }
        }

        private static void TryRemoveEmptyDirectory(string folder)
        {
            try
            {
                if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
                    Directory.Delete(folder, false);
            }
            catch { }
        }

        private static bool ScheduleDirectoryRemoval(string folder, out string error)
        {
            error = null;
            try
            {
                string script = Path.Combine(Path.GetTempPath(),
                    "typebridge-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
                string comspec = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrEmpty(comspec))
                    comspec = "cmd.exe";

                StringBuilder body = new StringBuilder();
                body.Append("@echo off\r\n");
                body.Append("ping -n 4 127.0.0.1 >nul\r\n");
                string escaped = folder.Replace("%", "%%");
                string[] exact =
                {
                    ExecutableName,
                    "typebridge.ini",
                    "typebridge.log",
                    "typebridge.log.1",
                    "typebridge-error.log"
                };
                foreach (string name in exact)
                    body.Append("del /f /q \"").Append(escaped).Append("\\")
                        .Append(name).Append("\" >nul 2>&1\r\n");
                body.Append("del /f /q \"").Append(escaped).Append("\\")
                    .Append(ExecutableName).Append(".old-*\" >nul 2>&1\r\n");
                body.Append("del /f /q \"").Append(escaped).Append("\\")
                    .Append(ExecutableName).Append(".new-*\" >nul 2>&1\r\n");
                // 只删空目录：目录里还有用户的别的文件就留着
                body.Append("rd \"").Append(escaped).Append("\" >nul 2>&1\r\n");
                body.Append("del /f /q \"%~f0\"\r\n");
                File.WriteAllText(script, body.ToString(), Encoding.Default);

                ProcessStartInfo info = new ProcessStartInfo(comspec, "/c \"" + script + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                error = "安排删除程序目录失败：" + ex.Message;
                return false;
            }
        }

        // ---------- 进程 ----------

        internal static bool IsAnotherInstanceRunning()
        {
            try
            {
                int current = Process.GetCurrentProcess().Id;
                foreach (Process process in Process.GetProcessesByName(AppName))
                {
                    try
                    {
                        if (process.Id != current)
                            return true;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>等待用户确认后关闭其他实例（卸载前用，避免两个进程抢同一个目录）。</summary>
        internal static int StopOtherInstances()
        {
            int stopped = 0;
            int current = Process.GetCurrentProcess().Id;
            foreach (Process process in Process.GetProcessesByName(AppName))
            {
                try
                {
                    if (process.Id == current)
                        continue;
                    process.Kill();
                    if (process.WaitForExit(3000))
                        stopped++;
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }
            return stopped;
        }

        internal static bool StartInstalled(string exePath, bool waitForExit, out string error)
        {
            error = null;
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(exePath);
                if (waitForExit)
                    info.Arguments = "--wait-for-exit";
                info.UseShellExecute = true;
                info.WorkingDirectory = Path.GetDirectoryName(exePath);
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---------- ShellLink COM ----------

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch,
                IntPtr findData, uint flags);
            void GetIDList(out IntPtr itemIdList);
            void SetIDList(IntPtr itemIdList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
                int cch, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid classId);
            [PreserveSig]
            int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName,
                [MarshalAs(UnmanagedType.Bool)] bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
        }
    }
}
