using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace TypeBridgeApp
{
    internal static class Tests
    {
        private static int assertions;
        private static void Check(bool condition, string name)
        {
            assertions++;
            if (!condition) throw new Exception(name);
        }
        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { assertions++; return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--edge-runtime")
            {
                try { EdgeSwitchChecks.Run(); }
                catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
                return;
            }
            if (args.Length > 0 && args[0] == "--runtime")
            {
                try { RuntimeChecks.Run(false); }
                catch (Exception error)
                {
                    Console.WriteLine(error.GetType().FullName + " " + error.Message);
                    Console.WriteLine(error.StackTrace);
                    if (error.InnerException != null) Console.WriteLine(error.InnerException.Message);
                    Environment.ExitCode = 1;
                }
                return;
            }
            List<string> lines = new List<string>();
            try { EdgeSwitchChecks.ReverseChecks(); }
            catch (Exception error) { Console.WriteLine(error.Message); Environment.ExitCode = 1; return; }
            LineFramer framer = new LineFramer();
            byte[] data = Encoding.UTF8.GetBytes("PING\nC \"中文🙂\"\r\n\nRM 10 -4\n");
            foreach (byte value in data) framer.Feed(new byte[] { value }, 1, lines.Add);
            Check(lines.Count == 3 && lines[1] == "C \"中文🙂\"", "fragmented Unicode");
            byte[] batched = Encoding.UTF8.GetBytes(new string('x', LineFramer.MaxBytes) + "\nPING\n");
            framer.Feed(batched, batched.Length, lines.Add);
            Check(lines.Count == 5 && lines[4] == "PING", "limit is per frame");
            Throws<InvalidDataException>(delegate { new LineFramer().Feed(new byte[LineFramer.MaxBytes + 1], LineFramer.MaxBytes + 1, lines.Add); });
            Throws<DecoderFallbackException>(delegate { new LineFramer().Feed(new byte[] { 255, 10 }, 2, lines.Add); });

            using (ControlQueue queue = new ControlQueue())
            {
                string received = null;
                Thread worker = new Thread(delegate() { received = queue.Take(); });
                worker.Start();
                Check(!worker.Join(40), "empty sender waits");
                Check(queue.TryAdd("K ctrl+a"), "enqueue");
                Check(worker.Join(1000) && received == "K ctrl+a", "enqueue wakes sender");
                queue.TryAdd("first"); queue.TryAdd("second");
                Check(queue.Take() == "first" && queue.Take() == "second", "FIFO order");
                worker = new Thread(delegate() { received = queue.Take(); });
                worker.Start();
                queue.Dispose();
                Check(worker.Join(1000) && received == null, "close wakes sender");
                Check(!queue.TryAdd("stale"), "closed session rejects input");
            }
            using (ControlQueue queue = new ControlQueue())
            {
                for (int i = 0; i < ControlQueue.MaxLines; i++) Check(queue.TryAdd("M 1 1"), "bounded enqueue");
                Check(!queue.TryAdd("overflow"), "line limit");
            }
            using (ControlQueue queue = new ControlQueue())
            {
                Check(queue.TryAdd(new string('x', ControlQueue.MaxCharacters)), "character budget boundary");
                Check(!queue.TryAdd("x"), "character budget enforced");
                queue.Dispose();
                Check(queue.Take() == null, "closed queue discards pending input");
            }
            Check(Program.ParseHostList("host, host；other").Length == 2, "legacy address list");
            Check(GlobalMouseHook.ShouldReturnToWindows("right", -1, 100, 1920, 1080), "right layout return edge");
            Check(!GlobalMouseHook.ShouldReturnToWindows("right", 100, 100, 1920, 1080), "inside remote screen");
            System.Diagnostics.ProcessStartInfo restart = AdministratorMode.RestartInfo(@"C:\Type Bridge\TypeBridge.exe");
            Check(restart.Verb == "runas" && restart.UseShellExecute, "restart requests Windows elevation");
            Check(restart.FileName == @"C:\Type Bridge\TypeBridge.exe" && restart.WorkingDirectory == @"C:\Type Bridge", "restart retains executable and config location");
            Check(restart.Arguments == "--wait-for-exit", "elevated child waits for the old instance");

            // ---- 安装 / 快捷方式 / 开机自启 ----
            Check(WindowsInstall.ExecutableFromCommand(
                    "\"C:\\Program Files\\TypeBridge\\TypeBridge.exe\" --autostart")
                == @"C:\Program Files\TypeBridge\TypeBridge.exe", "autostart command path");
            Check(!WindowsInstall.PathsEqual(@"C:\TypeBridge", @"C:\TypeBridge\sub"),
                "path equality rejects children");
            Check(WindowsInstall.AutoStartCommand(@"C:\A B\TypeBridge.exe")
                == "\"C:\\A B\\TypeBridge.exe\" --autostart", "autostart command quoting");

            string installRoot = Path.Combine(Path.GetTempPath(),
                "typebridge-install-" + Guid.NewGuid().ToString("N"));
            string registryRoot = @"Software\TypeBridgeTests\" + Guid.NewGuid().ToString("N");
            try
            {
                string sourceDir = Path.Combine(installRoot, "download");
                Directory.CreateDirectory(sourceDir);
                string sourceExe = Path.Combine(sourceDir, "TypeBridge.exe");
                File.WriteAllBytes(sourceExe, new byte[] { 77, 90, 1, 2, 3 });
                File.WriteAllText(Path.Combine(sourceDir, "typebridge.ini"), "ip=10.0.0.1\r\nport=4321");

                InstallLayout layout = new InstallLayout
                {
                    InstallDir = Path.Combine(installRoot, "app"),
                    StartMenuDir = Path.Combine(installRoot, "start-menu"),
                    DesktopDir = Path.Combine(installRoot, "desktop"),
                    RunKeyPath = registryRoot + @"\Run",
                    UninstallKeyPath = registryRoot + @"\Uninstall",
                    ValueName = "TypeBridgeTest"
                };

                string installed = null;
                string error = null;
                Check(WindowsInstall.Install(sourceExe, layout, true, true, out installed, out error),
                    "install completes: " + error);
                Check(WindowsInstall.PathsEqual(installed, layout.ExecutablePath), "install target path");
                Check(File.Exists(layout.ExecutablePath), "installed executable");
                Check(File.Exists(Path.Combine(layout.InstallDir, "typebridge.ini")), "config carried over");
                Check(File.Exists(layout.StartMenuShortcut), "start menu shortcut");
                Check(File.Exists(layout.DesktopShortcut), "desktop shortcut");
                Check(WindowsInstall.PathsEqual(
                        WindowsInstall.ReadShortcutTarget(layout.StartMenuShortcut), layout.ExecutablePath),
                    "shortcut points at installed exe");
                Check(WindowsInstall.IsAutoStartFor(
                        layout.ExecutablePath, layout.RunKeyPath, layout.ValueName),
                    "autostart registered");
                Check(WindowsInstall.IsInstalledAt(layout.ExecutablePath, layout),
                    "uninstall entry registered");

                // 重装不覆盖用户配置，并落实"不勾桌面快捷方式"
                File.WriteAllText(Path.Combine(layout.InstallDir, "typebridge.ini"), "ip=192.168.1.5");
                Check(WindowsInstall.Install(sourceExe, layout, false, false, out installed, out error),
                    "reinstall: " + error);
                Check(File.ReadAllText(Path.Combine(layout.InstallDir, "typebridge.ini")) == "ip=192.168.1.5",
                    "reinstall keeps user config");
                Check(!File.Exists(layout.DesktopShortcut), "unchecked desktop shortcut is removed");
                Check(!WindowsInstall.IsAutoStartFor(
                        layout.ExecutablePath, layout.RunKeyPath, layout.ValueName),
                    "autostart can be turned off");

                // 卸载只清自己放进去的文件，用户目录里的其他文件必须留下
                File.WriteAllText(Path.Combine(layout.InstallDir, "keep.txt"), "user data");
                Check(WindowsInstall.Uninstall(layout, sourceExe, out error),
                    "uninstall: " + error);
                Check(!File.Exists(layout.ExecutablePath), "uninstall removes executable");
                Check(File.Exists(Path.Combine(layout.InstallDir, "keep.txt")),
                    "uninstall keeps unrelated files");
                Check(Directory.Exists(layout.InstallDir), "non-empty folder is kept");
                Check(!File.Exists(layout.StartMenuShortcut) && !File.Exists(layout.DesktopShortcut),
                    "shortcuts removed");
                Check(WindowsInstall.ReadAutoStart(layout.RunKeyPath, layout.ValueName) == null,
                    "autostart removed");
                Check(!WindowsInstall.IsInstalledAt(layout.ExecutablePath, layout),
                    "uninstall entry removed");
            }
            finally
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\TypeBridgeTests", false); }
                catch { }
                try { if (Directory.Exists(installRoot)) Directory.Delete(installRoot, true); }
                catch { }
            }

            Console.WriteLine("PASS " + assertions + " Windows assertions");
        }
    }
}
