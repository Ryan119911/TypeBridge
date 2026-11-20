using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

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
            Console.WriteLine("PASS " + assertions + " Windows assertions");
        }
    }
}
