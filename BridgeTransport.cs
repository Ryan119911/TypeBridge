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
    internal sealed partial class BridgeApplicationContext
    {
        // ---------- 服务端下行消息 ----------

        private void StartReader(NetworkStream target)
        {
            StopReader();
            readerRunning = true;
            lastPongUtc = DateTime.UtcNow;

            // 始终下发：空值表示「用桌面」，否则守护进程会一直沿用上次的旧路径
            SendLine("DIR " + (remoteDir ?? string.Empty).Trim());
            // 上一次可能把对端指针藏起来了，重连先把可见性恢复
            SendLine("CUR 1");
            // 新连接先声明"本机没有在驱动对端"，对端据此决定能不能反向接管
            SendLine("OWN 0");
            // 反向共享用的信息：本机屏幕尺寸 + 本机在 Ubuntu 的哪一侧（决定它从哪条边推过来）
            SendLine("SW " + Screen.PrimaryScreen.Bounds.Width + " "
                + Screen.PrimaryScreen.Bounds.Height + " " + UbuntuRelativeSide());
            int generation = senderGeneration;
            readerThread = new System.Threading.Thread(delegate () { ReadLoop(target, generation); });
            readerThread.IsBackground = true;
            readerThread.Start();
        }

        private void StopReader()
        {
            readerRunning = false;
            readerThread = null;
        }

        private void ReadLoop(NetworkStream target, int generation)
        {
            byte[] buffer = new byte[8192];
            LineFramer framer = new LineFramer();
            try
            {
                while (readerRunning && generation == senderGeneration)
                {
                    int count = target.Read(buffer, 0, buffer.Length);
                    if (count <= 0) break;
                    framer.Feed(buffer, count, delegate(string line)
                    {
                        if (generation != senderGeneration) return;
                        int queued = System.Threading.Interlocked.Increment(ref queuedIncoming);
                        int characters = System.Threading.Interlocked.Add(ref queuedIncomingCharacters, line.Length);
                        try
                        {
                            if (queued > ControlQueue.MaxLines || characters > ControlQueue.MaxCharacters)
                                throw new IOException("Control receive queue overflow");
                            hotkeyForm.BeginInvoke(new Action(delegate
                            {
                                try
                                {
                                    if (!disposed && generation == senderGeneration) HandleServerLine(line);
                                }
                                finally
                                {
                                    System.Threading.Interlocked.Decrement(ref queuedIncoming);
                                    System.Threading.Interlocked.Add(ref queuedIncomingCharacters, -line.Length);
                                }
                            }));
                        }
                        catch
                        {
                            System.Threading.Interlocked.Decrement(ref queuedIncoming);
                            System.Threading.Interlocked.Add(ref queuedIncomingCharacters, -line.Length);
                            throw;
                        }
                    });
                }
            }
            catch (Exception ex) { Log.Write("reader stopped: " + ex.Message); }
            NotifyConnectionLost(generation);
        }

        private void HandleServerLine(string line)
        {
            if (line == "PONG")
            {
                lastPongUtc = DateTime.UtcNow;
                if (pingSentTs != 0)
                {
                    lastRttMs = (System.Diagnostics.Stopwatch.GetTimestamp() - pingSentTs)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    pingSentTs = 0;
                    // 延迟明显变高时记一笔，方便判断是不是被 TUN / 代理绕远了
                    if (lastRttMs > 120 && Environment.TickCount - lastSlowWarnTick > 20000)
                    {
                        lastSlowWarnTick = Environment.TickCount;
                        Log.Write("network slow: rtt=" + lastRttMs.ToString("0") + "ms");
                    }
                }
                return;
            }
            if (line.Length >= 2 && line[0] == 'S' && line[1] == ' ')
            {
                string[] parts = line.Substring(2).Split(' ');
                int width;
                int height;
                if (parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height)
                    && width > 0 && height > 0)
                {
                    remoteWidth = width;
                    remoteHeight = height;
                    mouseHook.RemoteWidth = width;
                    mouseHook.RemoteHeight = height;
                }
                return;
            }

            // ---------- 反向共享：Ubuntu 的键鼠操作本机 ----------
            // 按接收顺序在界面线程处理，接管、注入和断线清理共享同一状态。
            string[] fields = line.Split(' ');
            if (fields[0] == "RGW")
            {
                string action = fields.Length > 1 ? fields[1] : string.Empty;
                if (action == "1")
                {
                    int yNorm = 32768;
                    if (fields.Length > 2)
                        int.TryParse(fields[2], out yNorm);
                    BeginRemoteDrive(yNorm);
                }
                else if (action == "0")
                {
                    EndRemoteDrive("Ubuntu 交还控制", false);
                }
                return;
            }
            if (fields[0] == "RK" && fields.Length >= 3)
            {
                if (remoteDrive)
                    RemoteInput.Key(fields[1], fields[2] == "down");
                return;
            }
            if (fields[0] == "RM" && fields.Length >= 3)
            {
                int dx;
                int dy;
                if (remoteDrive && int.TryParse(fields[1], out dx) && int.TryParse(fields[2], out dy))
                    OnRemoteMouseMove(dx, dy);
                return;
            }
            if (fields[0] == "RB" && fields.Length >= 3)
            {
                int button;
                if (remoteDrive && int.TryParse(fields[1], out button))
                    RemoteInput.Button(button, fields[2] == "down");
                return;
            }
            if (fields[0] == "RW" && fields.Length >= 2)
            {
                if (remoteDrive)
                    RemoteInput.Wheel(fields[1]);
                return;
            }

            if (line == "BT")
            {
                // Ubuntu 端托盘里点了「切回 Windows」
                Log.Write("switch back requested by ubuntu tray");
                SetKeyboardMode(false, true);
                SetMouseMode(false, true);
                return;
            }

            if (line.StartsWith("DIR ", StringComparison.Ordinal))
            {
                // Ubuntu 端收到 DIR 后回的是"它实际会写进去的目录"
                remoteResolvedDir = line.Substring(4).Trim();
                Log.Write("ubuntu receive dir: " + remoteResolvedDir);
                return;
            }

            if (line.StartsWith("OPEN ", StringComparison.Ordinal))
            {
                string result = line.Substring(5).Trim();
                Log.Write("open dir on ubuntu: " + result);
                try
                {
                    hotkeyForm.BeginInvoke(new Action(delegate
                    {
                        ShowTrayTip("Ubuntu 接收目录",
                            result == "ok" ? (remoteResolvedDir.Length > 0 ? remoteResolvedDir : "已在 Ubuntu 上打开")
                                           : ("打开失败：" + result));
                    }));
                }
                catch { }
                return;
            }

            if (line.Length >= 2 && line[0] == 'C' && line[1] == ' ')
            {
                string text = null;
                try { text = json.Deserialize<string>(line.Substring(2)); }
                catch { }
                if (text == null)
                    return;
                ApplyRemoteClipboard(text);
            }
        }

        private static double MeasurePing(TcpClient client, int timeoutMs)
        {
            try
            {
                client.ReceiveTimeout = timeoutMs;
                NetworkStream socket = client.GetStream();
                byte[] question = Encoding.UTF8.GetBytes("PING\n");
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                socket.Write(question, 0, question.Length);
                socket.Flush();

                byte[] buffer = new byte[256];
                StringBuilder seen = new StringBuilder();
                // Environment.TickCount 是 int，加 timeoutMs 会溢出，且约 24.9 天回绕一次；
                // 用单调的 Stopwatch 计时就没有这个窗口。
                while ((System.Diagnostics.Stopwatch.GetTimestamp() - started)
                       * 1000.0 / System.Diagnostics.Stopwatch.Frequency < timeoutMs)
                {
                    int count = socket.Read(buffer, 0, buffer.Length);
                    if (count <= 0)
                        break;
                    seen.Append(Encoding.UTF8.GetString(buffer, 0, count));
                    if (seen.ToString().IndexOf("PONG", StringComparison.Ordinal) >= 0)
                    {
                        return (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    }
                }
                return -1;
            }
            catch
            {
                return -1;
            }
            finally
            {
                // 测完必须把接收超时清掉：这条连接会被直接拿去当正式会话，
                // 留着 700ms 超时的话，空闲超过 700ms 读取就抛异常，读线程直接退出，
                // 于是 PONG 再也收不到 —— 表现就是"鼠标在 Ubuntu 上但 3 秒后被强行收回"。
                try { client.ReceiveTimeout = 0; }
                catch { }
            }
        }

        private void ConnectToServer()
        {
            if (stream != null || connecting)
                return;
            connecting = true;
            int attempt = senderGeneration;
            int targetPort = port;
            string[] targets = hosts.Length > 0 ? hosts : new string[] { ip };
            string signature = string.Join(",", targets);

            // 连接放到线程池：原来 Wait(400) 每秒阻塞 UI 线程 400ms，
            // 期间全局钩子回调超时，按键鼠标会漏进 Windows。
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<TcpClient> opened = new List<TcpClient>();
                StringBuilder report = new StringBuilder();
                double bestRtt = double.MaxValue;
                TcpClient best = null;
                string bestHost = null;

                // 并行探测：TUN / 代理可能把某条路径拖到几百毫秒，另一条却是直连，
                // 所以每次都实测，用最快的那个，而不是死守配置里的第一个地址。
                // 先把所有候选的连接同时发出去，避免一条不通的地址拖慢整次连接
                List<System.Threading.Tasks.Task> pending = new List<System.Threading.Tasks.Task>();
                List<TcpClient> probes = new List<TcpClient>();
                foreach (string target in targets)
                {
                    TcpClient probe = new TcpClient();
                    try { probe.NoDelay = true; } catch { }
                    probes.Add(probe);
                    try { pending.Add(probe.ConnectAsync(target, targetPort)); }
                    catch { }
                }
                try
                {
                    if (pending.Count > 0)
                        System.Threading.Tasks.Task.WaitAll(pending.ToArray(), 900);
                }
                catch { }

                for (int index = 0; index < probes.Count; index++)
                {
                    string target = targets[index];
                    TcpClient probe = probes[index];
                    if (!probe.Connected)
                    {
                        try { probe.Close(); }
                        catch { }
                        report.Append(target + " 连不上；");
                        continue;
                    }

                    double rtt = MeasurePing(probe, 700);
                    if (rtt <= 0)
                    {
                        try { probe.Close(); }
                        catch { }
                        report.Append(target + " 无响应；");
                        continue;
                    }
                    report.Append(target + " " + rtt.ToString("0.0") + "ms；");
                    opened.Add(probe);
                    if (rtt < bestRtt)
                    {
                        bestRtt = rtt;
                        best = probe;
                        bestHost = target;
                    }
                }

                // 没选中的按顺序关掉，只留最快那条
                for (int i = 0; i < opened.Count; i++)
                {
                    if (!object.ReferenceEquals(opened[i], best))
                    {
                        try { opened[i].Close(); }
                        catch { }
                    }
                }

                TcpClient candidate = best;
                string host = bestHost;
                bool ok = candidate != null;
                if (ok)
                    Log.Write("path pick: " + host + ":" + targetPort + " rtt="
                        + bestRtt.ToString("0.0") + "ms  候选[" + report + "]");
                else
                    Log.Write("connect failed: " + report);

                try
                {
                    string captured = signature;
                    hotkeyForm.BeginInvoke(new MethodInvoker(delegate
                    {
                        connecting = false;
                        string currentSignature = string.Join(",",
                            hosts.Length > 0 ? hosts : new string[] { ip });
                        if (disposed || attempt != senderGeneration || !ok || stream != null || currentSignature != captured || targetPort != port)
                        {
                            if (ok)
                            {
                                try { candidate.Close(); }
                                catch { }
                            }
                            return;
                        }
                        client = candidate;
                        stream = candidate.GetStream();
                        activeHost = host;
                        lastRttMs = bestRtt;
                        // 刚连上不能立刻被看门狗判死
                        lastPongUtc = DateTime.UtcNow;
                        Log.Write("connected " + host + ":" + targetPort
                            + " (" + bestRtt.ToString("0.0") + "ms)");
                        // 写超时：半死链路上 Write 最多卡 4 秒就报错，
                        // 不至于把 UI 线程外的发送线程卡几十秒才判死
                        try { stream.WriteTimeout = 4000; }
                        catch { }
                        EnableKeepAlive(candidate);
                        StartSender();
                        StartReader(stream);
                        // 边缘切换开启且鼠标还在本机时，让鼠标钩子开始盯屏幕边缘
                        mouseHook.Watching = edgeSwitch && !mouseMode;
                        UpdateStatus();

                        // 设置窗口保存会先断开再异步重连；等 stream 真正就绪才恢复，
                        // 否则 Set*Mode 的 stream==null 守卫会把恢复挡掉。
                        if (resumeKeyboardAfterReconnect || resumeMouseAfterReconnect)
                        {
                            bool wantKeyboard = resumeKeyboardAfterReconnect;
                            bool wantMouse = resumeMouseAfterReconnect;
                            resumeKeyboardAfterReconnect = false;
                            resumeMouseAfterReconnect = false;
                            SetKeyboardMode(wantKeyboard, false);
                            if (wantMouse)
                                SetMouseMode(true, false);
                        }
                    }));
                }
                catch
                {
                    // 句柄销毁导致 BeginInvoke 抛异常时，connecting 会永远卡在 true，
                    // 之后每秒的重连都被守卫挡住。这里复位并关掉已探测好的候选连接。
                    connecting = false;
                    if (candidate != null)
                    {
                        try { candidate.Close(); }
                        catch { }
                    }
                }
            });
        }

        /// <summary>5 秒空闲后每 2 秒发 TCP 探测包，死链路几秒内触发写失败，
        /// 而不是等系统默认的探测周期（可达数分钟）。</summary>
        private static void EnableKeepAlive(TcpClient tcp)
        {
            try
            {
                // SIO_KEEPALIVE_VALS 是 3 个 32 位 u_long（onoff / time / interval），
                // 共 12 字节。原来用 ulong(8B) 写到 offset 8 会越界抛异常被 catch 吞掉，
                // 结果 keepalive 从未生效，死链路只能靠写超时发现。
                byte[] values = new byte[12];
                BitConverter.GetBytes((uint)1).CopyTo(values, 0);
                BitConverter.GetBytes((uint)5000).CopyTo(values, 4);
                BitConverter.GetBytes((uint)2000).CopyTo(values, 8);
                tcp.Client.IOControl(IOControlCode.KeepAliveValues, values, null);
            }
            catch { }
        }

        private void StartSender()
        {
            NetworkStream target = stream;
            if (outgoing != null) outgoing.Dispose();
            ControlQueue queue = new ControlQueue();
            outgoing = queue;
            int generation = ++senderGeneration;
            System.Threading.Thread senderThread = new System.Threading.Thread(
                delegate() { SenderLoop(target, queue, generation); });
            senderThread.IsBackground = true;
            senderThread.Name = "TypeBridge-Sender";
            senderThread.Start();
        }

        private void SenderLoop(NetworkStream target, ControlQueue queue, int generation)
        {
            while (senderGeneration == generation)
            {
                string line = queue.Take();
                if (line == null) return;
                if (target == null || senderGeneration != generation)
                    continue;
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                    target.Write(bytes, 0, bytes.Length);
                    target.Flush();
                }
                catch
                {
                    NotifyConnectionLost(generation);
                    return;
                }
            }
        }

        /// <summary>发送线程发现断链后回到 UI 线程收尾：断开并把键鼠切回 Windows。
        /// 这是兜底保护——即使切换热键在断链期间失灵，用户也不会被卡在 Ubuntu 模式。</summary>
        private void NotifyConnectionLost(int generation)
        {
            // 旧世代的发送线程写失败时不能动刚建好的新连接
            if (generation != senderGeneration)
                return;
            try
            {
                hotkeyForm.BeginInvoke(new MethodInvoker(delegate
                {
                    if (disposed || generation != senderGeneration) return;
                    bool wasActive = keyboardMode || mouseMode || remoteDrive;
                    Log.Write("connection lost (wasActive=" + wasActive + ")");
                    CloseConnection();
                    if (mouseMode)
                        SetMouseMode(false, false);
                    if (keyboardMode)
                        SetKeyboardMode(false, false);
                    mouseMode = false;
                    UpdateStatus();
                    if (wasActive)
                        ShowTrayTip("连接已断开", "键盘鼠标已切回 Windows，自动重连中");
                }));
            }
            catch { }
        }

        /// <summary>
        /// 输入自愈：低级键盘/鼠标钩子有概率被 Windows 静默摘除（回调偶发超时被摘，
        /// 进程不收到任何通知，句柄值却仍非 0）。一旦发生，按热键和推边缘都石沉大海，
        /// 而进程、TCP 连接、网络看门狗全部正常 —— 只能靠这里定期强制重装恢复。
        ///
        /// 只在非接管态、非设置窗口时做：接管态正在转发输入，重装会漏事件；
        /// 设置窗口本来就主动摘了热键和钩子，不去打扰。重注册热键可顺带恢复被
        /// 其他程序挤掉后一直没回来的全局热键。
        /// </summary>
        private void SelfHealInput()
        {
            if (keyboardMode || mouseMode || remoteDrive || settingsOpen)
                return;
            if ((DateTime.UtcNow - lastHookHealUtc).TotalSeconds < 30)
                return;
            lastHookHealUtc = DateTime.UtcNow;
            try
            {
                hook.Reinstall();
                mouseHook.Reinstall();
                ApplyHotkeys();
                Log.Write("input self-heal: hooks + hotkeys reinstalled");
            }
            catch (Exception ex)
            {
                Log.Write("input self-heal failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 每秒一次：发心跳；半开连接收不到 PONG 就把键鼠还给本机，
        /// 避免"网络断了但 TCP 没报错"时人被困在 Ubuntu 模式。
        /// </summary>
        private void ConnectionTick()
        {
            if (!receiverRunning && DateTime.UtcNow >= nextFileReceiverAttemptUtc)
                StartFileReceiver();
            SelfHealInput();
            if (stream != null)
            {
                pingSentTs = System.Diagnostics.Stopwatch.GetTimestamp();
                SendLine("PING");

                // 接管态 3 秒兜底（不能把人困在远端）；Windows 模式下放宽到 8 秒。
                // 3.6.2 曾让两种状态都用 3 秒：一次 4 秒的网络抖动就触发断线，
                // 而链路恢复前重连要十几秒，期间 Watching=false、边缘切屏完全没反应，
                // 表现就是「推两下没成功，隔一阵才切过去」。
                // 链路真死时 SenderLoop 的 WriteTimeout(4s) 会走 NotifyConnectionLost 兜底。
                bool activeMode = keyboardMode || mouseMode || remoteDrive;
                double limitSeconds = activeMode ? 3 : 8;
                double silentSeconds = (DateTime.UtcNow - lastPongUtc).TotalSeconds;
                if (silentSeconds > limitSeconds)
                {
                    Log.Write("watchdog: no pong for " + silentSeconds.ToString("0.0")
                        + "s (limit=" + limitSeconds.ToString("0") + "), reconnect (wasActive="
                        + activeMode + ")");
                    if (activeMode)
                    {
                        if (remoteDrive)
                        {
                            ShowTrayTip("已收回控制", "和 Ubuntu 的连接断了，键盘鼠标回到本机");
                            EndRemoteDrive("连接超时", false);
                        }
                        else
                        {
                            ShowTrayTip("已切回本机", "没收到 Ubuntu 回应，先把键盘鼠标还给你");
                        }
                        SetKeyboardMode(false, false);
                        SetMouseMode(false, false);
                    }
                    CloseConnection();
                    return;
                }
            }

            ConnectToServer();
        }

        private void CloseConnection()
        {
            // 反向接管中把键放掉：否则 Ubuntu 一断网，本机就卡在"Ctrl 一直按着"
            remoteDrive = false;
            mouseHook.LocalWatch = false;
            hook.LocalWatch = false;
            RemoteInput.ReleaseAll();
            keyboardMode = false;
            hook.Enabled = false;
            // 先补发鼠标 up，再断开，避免 Ubuntu 端卡在拖拽状态
            mouseHook.ReleaseAll();
            StopReader();
            mouseHook.Watching = false;
            try
            {
                if (client != null)
                    client.Close();
            }
            catch { }
            client = null;
            stream = null;
            // 作废旧发送线程：它持有自己的队列与流，醒来后看到世代不符就退出
            senderGeneration++;
            if (outgoing != null) outgoing.Dispose();
            outgoing = null;
            mouseHook.Connected = false;
            mouseHook.RestoreCursor();
            // 断了还留在 Ubuntu 模式 = 把用户锁在远端：立刻还给本机
            if (mouseMode)
            {
                mouseMode = false;
                mouseHook.Enabled = false;
                UpdateStatus();
            }
        }

        /// <summary>只把指令排队，立即返回。真正的网络写在后台发送线程里。</summary>
        private bool SendLine(string line)
        {
            if (stream == null || outgoing == null) return false;
            if (!outgoing.TryAdd(line))
            {
                NotifyConnectionLost(senderGeneration);
                return false;
            }
            lastEnqueueUtc = DateTime.UtcNow;
            return true;
        }

        private bool SendText(string text)
        {
            if (text.Length == 0)
                return true;
            return SendLine("T " + json.Serialize(text));
        }

    }
}
