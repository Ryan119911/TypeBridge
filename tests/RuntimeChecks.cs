using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TypeBridgeApp
{
    // Compiled against both the original and refactored sources for comparison.
    internal static class RuntimeChecks
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
        private static BridgeApplicationContext context;
        private static volatile TcpClient peer;
        private static volatile bool running = true;

        private static object Field(string name)
        {
            return typeof(BridgeApplicationContext).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(context);
        }
        private static void Pump(int milliseconds)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }
        private static void Until(Func<bool> predicate, string name)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!predicate() && watch.ElapsedMilliseconds < 10000) Pump(20);
            if (!predicate()) throw new Exception(name);
        }
        private static void Serve(TcpListener listener)
        {
            while (running)
            {
                try
                {
                    using (TcpClient incoming = listener.AcceptTcpClient())
                    {
                        peer = incoming;
                        StreamReader reader = new StreamReader(incoming.GetStream(), Encoding.UTF8);
                        StreamWriter writer = new StreamWriter(incoming.GetStream(), new UTF8Encoding(false));
                        writer.AutoFlush = true;
                        writer.WriteLine("S 1280 800");
                        string line;
                        while (running && (line = reader.ReadLine()) != null)
                            if (line == "PING") writer.WriteLine("PONG");
                    }
                }
                catch (SocketException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            try { Run(args.Length > 0 && args[0] == "baseline"); }
            catch (Exception error)
            {
                Console.WriteLine(error.GetType().FullName);
                Console.WriteLine(error.Message);
                Console.WriteLine(error.StackTrace);
                if (error.InnerException != null) Console.WriteLine(error.InnerException.Message);
                Environment.ExitCode = 1;
            }
        }

        internal static void Run(bool baseline)
        {
            if (Environment.GetEnvironmentVariable("TYPEBRIDGE_TEST_DESKTOP") != "1")
                throw new InvalidOperationException("Launch runtime checks through the private desktop runner");
            if (baseline) timeBeginPeriod(1);
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            TcpListener fileProbe = new TcpListener(IPAddress.Any, 0);
            fileProbe.ExclusiveAddressUse = true;
            fileProbe.Start();
            int filePort = ((IPEndPoint)fileProbe.LocalEndpoint).Port;
            if (baseline) fileProbe.Stop();
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string ini = Path.Combine(directory, "typebridge.ini");
            if (File.Exists(ini)) throw new IOException("Use a dedicated empty output directory for runtime checks");
            File.WriteAllLines(ini, new string[] {
                "ip=127.0.0.1", "port=" + port, "file_recv_port=" + filePort,
                "recv_dir=" + Path.Combine(directory, "received"), "edge_switch=0", "mouse_with_keyboard=0",
                "keyboard_hotkey=Ctrl+Alt+F24", "mouse_hotkey=Ctrl+Alt+F23"
            });
            Thread server = new Thread(delegate() { Serve(listener); });
            server.IsBackground = true;
            server.Start();
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Console.WriteLine("Creating application context");
                context = new BridgeApplicationContext();
                Console.WriteLine("Application context ready");
                if (!baseline)
                {
                    if ((bool)Field("receiverRunning")) throw new Exception("occupied file port should fail initially");
                    fileProbe.Stop();
                    Until(delegate { return (bool)Field("receiverRunning"); }, "file receiver retries after port is released");
                    Console.WriteLine("PASS file receiver recovers after temporary port conflict");
                }
                Until(delegate { return Field("stream") != null; }, "runtime connects");
                if (!baseline)
                {
                    typeof(BridgeApplicationContext).GetMethod("SetKeyboardMode", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(context, new object[] { true, false });
                    if (!(bool)Field("keyboardMode")) throw new Exception("keyboard mode activation");
                    peer.Close();
                    Until(delegate { return !(bool)Field("keyboardMode"); }, "disconnect releases keyboard mode");
                    Until(delegate { return Field("stream") != null; }, "automatic reconnect");
                    Console.WriteLine("PASS runtime startup, heartbeat, keyboard disconnect recovery and reconnect");
                }
                Pump(5000);
                // Use the normal message loop during sampling, without a test polling timer.
                Process process = Process.GetCurrentProcess();
                process.Refresh();
                TimeSpan cpu = process.TotalProcessorTime;
                long privateStart = process.PrivateMemorySize64;
                int handlesStart = process.HandleCount;
                Stopwatch elapsed = Stopwatch.StartNew();
                System.Windows.Forms.Timer finish = new System.Windows.Forms.Timer();
                finish.Interval = 30000;
                finish.Tick += delegate {
                finish.Stop();
                process.Refresh();
                Console.WriteLine("METRICS label={0} seconds={1:F2} cpu_ms={2:F2} cpu_one_core_pct={3:F4} working_set_mib={4:F2} private_mib={5:F2} private_delta_kib={6:F0} handles={7} handles_delta={8} threads={9}",
                    baseline ? "baseline" : "refactored", elapsed.Elapsed.TotalSeconds,
                    (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    (process.TotalProcessorTime - cpu).TotalSeconds / elapsed.Elapsed.TotalSeconds * 100,
                    process.WorkingSet64 / 1048576.0, process.PrivateMemorySize64 / 1048576.0,
                    (process.PrivateMemorySize64 - privateStart) / 1024.0,
                    process.HandleCount, process.HandleCount - handlesStart, process.Threads.Count);
                context.ExitThread();
                };
                finish.Start();
                Application.Run(context);
                finish.Dispose();
            }
            finally
            {
                if (context != null) context.ExitThread();
                running = false;
                if (peer != null) peer.Close();
                listener.Stop();
                fileProbe.Stop();
                File.Delete(ini);
                if (baseline) timeEndPeriod(1);
            }
        }
    }
}
