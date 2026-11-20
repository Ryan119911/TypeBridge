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
        // ---------- 文件发送 ----------

        // ---------- 文件拖放区与接收 ----------

        /// <summary>按住左键拖到判定线时：在判定线内侧弹出玻璃传送区。</summary>
        private void ShowFileZone()
        {
            if (mouseMode)
                return;

            if (fileZone == null || fileZone.IsDisposed)
                fileZone = new FileZoneForm(delegate (string[] paths) { HideFileZone(); UploadFiles(paths); });

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Point cursor = Cursor.Position;
            int x;
            int y;

            switch (switchEdge)
            {
                case "left":
                    x = area.Left + UiScale.S(28);
                    y = cursor.Y - fileZone.Height / 2;
                    break;
                case "right":
                    x = area.Right - fileZone.Width - UiScale.S(28);
                    y = cursor.Y - fileZone.Height / 2;
                    break;
                case "top":
                    x = cursor.X - fileZone.Width / 2;
                    y = area.Top + UiScale.S(28);
                    break;
                default:
                    x = cursor.X - fileZone.Width / 2;
                    y = area.Bottom - fileZone.Height - UiScale.S(28);
                    break;
            }

            x = Math.Max(area.Left + 8, Math.Min(area.Right - fileZone.Width - 8, x));
            y = Math.Max(area.Top + 8, Math.Min(area.Bottom - fileZone.Height - 8, y));
            fileZone.Location = new Point(x, y);

            if (!fileZone.Visible)
                fileZone.Show();
            fileZone.BringToFront();

            if (fileZoneTimer == null)
            {
                fileZoneTimer = new Timer();
                fileZoneTimer.Interval = 4000;
                fileZoneTimer.Tick += delegate { fileZoneTimer.Stop(); HideFileZone(); };
            }
            fileZoneTimer.Stop();
            fileZoneTimer.Start();
        }

        private void HideFileZone()
        {
            if (fileZoneTimer != null)
                fileZoneTimer.Stop();
            if (fileZone != null && fileZone.Visible)
                fileZone.Hide();
        }

        private void ToggleDropPad()
        {
            if (dropPad == null || dropPad.IsDisposed)
            {
                dropPad = new DropPadForm(delegate (string[] paths) { UploadFiles(paths); });
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                dropPad.Location = new Point(area.Right - dropPad.Width - 32, area.Bottom - dropPad.Height - 32);
            }

            if (dropPad.Visible)
            {
                dropPad.Hide();
            }
            else
            {
                dropPad.Show();
                dropPad.BringToFront();
            }
        }

        private DateTime nextFileReceiverAttemptUtc;

        private void StartFileReceiver()
        {
            if (disposed || receiverRunning) return;
            nextFileReceiverAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            try
            {
                fileListener = new TcpListener(System.Net.IPAddress.Any, fileRecvPort);
                fileListener.Start();
                receiverRunning = true;
                System.Threading.Thread thread = new System.Threading.Thread(FileAcceptLoop);
                thread.IsBackground = true;
                thread.Start();
                Log.Write("file receiver on " + fileRecvPort);
            }
            catch (Exception ex)
            {
                if (fileListener != null) fileListener.Stop();
                fileListener = null;
                Log.Write("file receiver failed: " + ex.Message);
            }
        }

        private void FileAcceptLoop()
        {
            while (receiverRunning)
            {
                TcpClient incoming = null;
                try
                {
                    incoming = fileListener.AcceptTcpClient();
                }
                catch
                {
                    return;
                }

                TcpClient target = incoming;
                System.Threading.Thread worker = new System.Threading.Thread(delegate () { ReceiveUpload(target); });
                worker.IsBackground = true;
                worker.Start();
            }
        }

        private void ReceiveUpload(TcpClient incoming)
        {
            try
            {
                incoming.ReceiveTimeout = 60000;
                NetworkStream source = incoming.GetStream();

                string header = ReadAsciiLine(source);
                string[] parts = header.Split(new char[] { ' ' }, 3);
                bool clipImage = parts.Length == 3 && parts[0] == "CLIPIMG";
                bool resumeQuery = parts.Length == 3 && parts[0] == "RESUME";
                if (parts.Length != 3 || (parts[0] != "UPLOAD" && !clipImage && !resumeQuery))
                {
                    SendAscii(source, "ERR bad header");
                    return;
                }

                long size;
                if (!long.TryParse(parts[1], out size) || size < 0)
                {
                    SendAscii(source, "ERR bad size");
                    return;
                }

                if (clipImage)
                {
                    // 剪贴板图片：读进内存直接上剪贴板，不落盘。
                    // 第三段是可选文本长度；旧版对端这里放的是文件名，解析失败按 0 处理。
                    long captionLength;
                    if (!long.TryParse(parts[2], out captionLength) || captionLength < 0)
                        captionLength = 0;
                    // 图片和文本都攒在内存里，畸形长度头足以把进程撑爆
                    if (size > MaxClipImageBytes || captionLength > MaxClipCaptionBytes)
                    {
                        SendAscii(source, "ERR clipboard too large");
                        return;
                    }

                    byte[] png;
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        long remainingImage = size;
                        byte[] chunk = new byte[65536];
                        while (remainingImage > 0)
                        {
                            int count = source.Read(chunk, 0, (int)Math.Min(chunk.Length, remainingImage));
                            if (count <= 0)
                                break;
                            buffer.Write(chunk, 0, count);
                            remainingImage -= count;
                        }
                        if (remainingImage != 0)
                        {
                            SendAscii(source, "ERR incomplete");
                            return;
                        }
                        png = buffer.ToArray();
                    }

                    string caption = string.Empty;
                    if (captionLength > 0)
                    {
                        using (MemoryStream buffer = new MemoryStream())
                        {
                            long remainingText = captionLength;
                            byte[] chunk = new byte[65536];
                            while (remainingText > 0)
                            {
                                int count = source.Read(chunk, 0, (int)Math.Min(chunk.Length, remainingText));
                                if (count <= 0)
                                    break;
                                buffer.Write(chunk, 0, count);
                                remainingText -= count;
                            }
                            if (remainingText != 0)
                            {
                                SendAscii(source, "ERR incomplete");
                                return;
                            }
                            caption = Encoding.UTF8.GetString(buffer.ToArray());
                        }
                    }

                    try
                    {
                        string payloadText = caption;
                        hotkeyForm.BeginInvoke(new Action(delegate
                        {
                            ApplyRemoteClipboardImage(png, payloadText);
                        }));
                    }
                    catch { }
                    SendAscii(source, "OK clipboard");
                    return;
                }

                string directory = ResolveRecvDir();
                // 目标目录（如 下载\typebridge）可能不存在，写 .part 前先建好，
                // 否则 FileStream 抛 DirectoryNotFoundException，发送端只会收到 ERR
                try { System.IO.Directory.CreateDirectory(directory); }
                catch (Exception ex)
                {
                    SendAscii(source, "ERR " + ex.Message);
                    return;
                }

                // 第三段是 "文件名 [续传偏移]"。文件名本身可能以「空格 + 数字」
                // 结尾（如 "IMG 2"），所以只有确实存在匹配的断点记录才把它当偏移。
                string raw = parts[2];
                string name = SafeFileName(raw);
                long offset = 0;
                int space = raw.LastIndexOf(' ');
                if (space > 0)
                {
                    string tail = raw.Substring(space + 1);
                    long candidate;
                    if (long.TryParse(tail, out candidate) && candidate > 0)
                    {
                        string candidateName = SafeFileName(raw.Substring(0, space));
                        string candidateDest = System.IO.Path.Combine(directory, candidateName);
                        if (ReadResumeOffset(candidateDest, candidateName, size) == candidate)
                        {
                            name = candidateName;
                            offset = candidate;
                        }
                    }
                }

                string dest = System.IO.Path.Combine(directory, name);
                string temp = dest + ".part";

                if (resumeQuery)
                {
                    // "RESUME <总大小> <文件名>" 第三段整体是文件名，不带偏移；
                    // 走上面按空格拆偏移的逻辑会把 "IMG 2" 误截成 "IMG"。
                    string queryName = SafeFileName(raw);
                    string queryDest = System.IO.Path.Combine(directory, queryName);
                    SendAscii(source, "HAVE " + ReadResumeOffset(queryDest, queryName, size));
                    return;
                }

                // 同名文件并发上传会共写同一个 .part，按目标路径串行化
                object gate;
                lock (uploadGates)
                {
                    if (!uploadGates.TryGetValue(dest, out gate))
                    {
                        gate = new object();
                        uploadGates[dest] = gate;
                    }
                }

                lock (gate)
                {
                    if (offset > 0 && ReadResumeOffset(dest, name, size) != offset)
                        offset = 0;

                    long remaining = size - offset;
                    using (FileStream output = new FileStream(temp,
                        offset > 0 ? FileMode.Open : FileMode.Create,
                        FileAccess.Write, FileShare.None))
                    {
                        if (offset > 0)
                        {
                            output.Seek(offset, SeekOrigin.Begin);
                            output.SetLength(offset);
                        }
                        else
                        {
                            WriteResumeMeta(dest, name, size);
                        }

                        byte[] buffer = new byte[65536];
                        while (remaining > 0)
                        {
                            int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (count <= 0)
                                break;
                            output.Write(buffer, 0, count);
                            remaining -= count;
                        }
                    }

                    if (remaining != 0)
                    {
                        // 保留 .part 与 .part.info，下次重发同一个文件接着传
                        SendAscii(source, "ERR incomplete");
                        return;
                    }

                    // 原子替换：不会像 Delete+Move 那样在异常时把旧文件和新数据一起丢掉
                    try
                    {
                        if (File.Exists(dest))
                            File.Replace(temp, dest, null);
                        else
                            File.Move(temp, dest);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        if (File.Exists(dest))
                            File.Delete(dest);
                        File.Move(temp, dest);
                    }
                    try { File.Delete(PartMetaPath(dest)); }
                    catch { }

                    SendAscii(source, "OK " + dest);
                }
                string saved = name;
                try
                {
                    hotkeyForm.BeginInvoke(new Action(delegate
                    {
                        ShowTrayTip("收到文件", saved + " → " + directory);
                    }));
                }
                catch { }
            }
            catch (Exception ex)
            {
                try { SendAscii(incoming.GetStream(), "ERR " + ex.Message); }
                catch { }
            }
            finally
            {
                try { incoming.Close(); }
                catch { }
            }
        }

        private string ResolveRecvDir()
        {
            string configured = (recvDir ?? string.Empty).Trim();
            if (configured.Length > 0)
                return Environment.ExpandEnvironmentVariables(configured);
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "typebridge");
        }

        private const long MaxClipImageBytes = 64L * 1024 * 1024;
        private const long MaxClipCaptionBytes = 1024L * 1024;

        private static string PartMetaPath(string dest)
        {
            return dest + ".part.info";
        }

        private void WriteResumeMeta(string dest, string name, long size)
        {
            try
            {
                Dictionary<string, object> meta = new Dictionary<string, object>();
                meta["name"] = name;
                meta["size"] = size;
                System.IO.File.WriteAllText(PartMetaPath(dest), json.Serialize(meta),
                                           new UTF8Encoding(false));
            }
            catch { }
        }

        private long ReadResumeOffset(string dest, string name, long size)
        {
            try
            {
                string part = dest + ".part";
                if (!System.IO.File.Exists(part) || !System.IO.File.Exists(PartMetaPath(dest)))
                    return 0;
                Dictionary<string, object> meta = json.Deserialize<Dictionary<string, object>>(
                    System.IO.File.ReadAllText(PartMetaPath(dest)));
                if (meta == null || !meta.ContainsKey("name") || !meta.ContainsKey("size"))
                    return 0;
                if ((meta["name"] as string) != name || Convert.ToInt64(meta["size"]) != size)
                    return 0;
                long have = new System.IO.FileInfo(part).Length;
                if (have <= 0 || have >= size)
                    return 0;
                return have;
            }
            catch { return 0; }
        }

        private static string SafeFileName(string name)
        {
            string baseName = System.IO.Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
            foreach (char bad in System.IO.Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(bad, '_');
            // GetInvalidFileNameChars 不含 Windows 保留设备名，也不管尾部空格/点；
            // "CON.part" 会直接落到设备 CON 上，File.Move 抛异常并留下 .part。
            baseName = baseName.TrimEnd(' ', '.');
            string stem = System.IO.Path.GetFileNameWithoutExtension(baseName).ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
                || (stem.Length == 4 && stem.StartsWith("COM") && stem[3] >= '1' && stem[3] <= '9')
                || (stem.Length == 4 && stem.StartsWith("LPT") && stem[3] >= '1' && stem[3] <= '9'))
                baseName = "_" + baseName;
            if (baseName.Length == 0 || baseName == "." || baseName == "..")
                baseName = "file";
            return baseName;
        }

        private static string ReadAsciiLine(NetworkStream source)
        {
            List<byte> bytes = new List<byte>();
            while (bytes.Count < 4096)
            {
                int value = source.ReadByte();
                if (value < 0 || value == '\n')
                    break;
                if (value != '\r')
                    bytes.Add((byte)value);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static void SendAscii(NetworkStream target, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text + "\n");
            target.Write(data, 0, data.Length);
            target.Flush();
        }

        private void SendFilesDialog()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要发送到 Ubuntu 的文件";
                dialog.Multiselect = true;
                dialog.CheckFileExists = true;
                dialog.RestoreDirectory = true;
                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                string[] files = dialog.FileNames;
                if (files.Length == 0)
                    return;

                // 上传放到后台线程（UploadFiles 内部起线程），否则大文件会把 UI 和任务栏冻住
                UploadFiles(files);
            }
        }

        /// <summary>UI 线程入口：先建好进度窗口，再把真正的传输丢到后台。</summary>
        private void UploadFiles(string[] files)
        {
            TransferProgressForm progress = ProgressForm();
            progress.BeginTransfer();
            System.Threading.Thread worker = new System.Threading.Thread(
                delegate () { UploadWorker(files, progress); });
            worker.IsBackground = true;
            worker.Start();
        }

        private void UploadWorker(string[] files, TransferProgressForm progress)
        {
            int ok = 0;
            string firstProblem = null;
            string lastDestination = null;

            foreach (string file in files)
            {
                if (progress.CancelRequested)
                {
                    if (firstProblem == null)
                        firstProblem = "已取消";
                    break;
                }

                string error;
                string destination;
                if (UploadOne(file, progress, out error, out destination))
                {
                    ok++;
                    if (destination != null)
                        lastDestination = destination;
                }
                else if (firstProblem == null)
                {
                    firstProblem = System.IO.Path.GetFileName(file) + "：" + (error ?? "发送失败");
                }
            }

            // 落地路径必须回显：之前只说"已发送"，用户根本不知道文件进了 Ubuntu 的哪个目录
            string message = "已发送 " + ok + " / " + files.Length + " 个文件到 Ubuntu";
            if (ok > 0)
            {
                if (files.Length == 1 && lastDestination != null)
                    message += " → " + lastDestination;
                else if (lastDestination != null)
                    message += " → " + System.IO.Path.GetDirectoryName(lastDestination);
            }
            if (firstProblem != null)
                message += "（" + firstProblem + "）";

            progress.Finish(message, false);

            try
            {
                hotkeyForm.BeginInvoke(new Action(delegate
                {
                    UpdateStatus();
                    ShowTrayTip("发送文件", message);
                }));
            }
            catch { }
        }

        private TransferProgressForm progressForm;

        private TransferProgressForm ProgressForm()
        {
            if (progressForm == null || progressForm.IsDisposed)
                progressForm = new TransferProgressForm();
            return progressForm;
        }

        private bool UploadOne(string path, TransferProgressForm progress,
                               out string error, out string destination)
        {
            // ip 是 ini 原始串（可能含逗号），直接交给 ConnectAsync 必然失败。
            // 优先走控制连接实测最快的地址，其余候选依次兜底。
            string lastProblem = null;
            foreach (string host in FileTargetHosts())
            {
                if (FileSender.Upload(host, filePort, path, progress, out error, out destination))
                    return true;
                lastProblem = error;
            }
            error = lastProblem ?? "连不上文件端口 " + filePort;
            destination = null;
            return false;
        }

        /// <summary>文件通道该连哪个地址：已选路的控制连接优先，其余候选兜底。</summary>
        private string[] FileTargetHosts()
        {
            List<string> list = new List<string>();
            if (!string.IsNullOrEmpty(activeHost))
                list.Add(activeHost);
            string[] all = hosts != null && hosts.Length > 0
                ? hosts : Program.ParseHostList(ip);
            foreach (string host in all)
                if (!list.Contains(host))
                    list.Add(host);
            return list.ToArray();
        }

        private static string ReadReply(TcpClient socket)
        {
            return FileSender.ReadReply(socket);
        }

        /// <summary>把 ip 拆成候选地址列表；与 CLI 共用同一套分隔符。</summary>
        private void RefreshHosts()
        {
            hosts = Program.ParseHostList(ip);
        }

        /// <summary>在已经连上的 socket 上打一次 PING，返回往返毫秒；失败返回 -1。</summary>
    }
}
