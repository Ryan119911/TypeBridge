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
    internal static class FileSender
    {
        /// <summary>把文件推给 Ubuntu 的文件端口，顺带回调进度。UI 和命令行共用这一份实现。</summary>
        internal static bool Upload(string ip, int port, string path, TransferProgressForm progress,
                                    out string error, out string destination)
        {
            error = null;
            destination = null;
            TcpClient socket = null;
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists)
                {
                    error = "文件不存在";
                    return false;
                }

                socket = new TcpClient();
                socket.NoDelay = true;
                // 大文件传输最怕默默挂死：写超过 60 秒没有进展就判死，让上层拿到明确的错误
                socket.SendTimeout = 60000;
                if (!socket.ConnectAsync(ip, port).Wait(4000))
                {
                    error = "连不上文件端口 " + port;
                    Log.Write("send file failed: " + info.Name + " " + error);
                    return false;
                }

                Log.Write("send file: " + info.Name + " (" + info.Length + " bytes) -> "
                    + ip + ":" + port);

                // 先问对端已经收到多少：新版本回 HAVE <字节数>，旧版本回 ERR bad header，
                // 拿不到就按"从头传"走老格式，保持兼容。
                long offset = 0;
                bool resumeSupported = false;
                try
                {
                    using (TcpClient probe = new TcpClient())
                    {
                        probe.NoDelay = true;
                        if (probe.ConnectAsync(ip, port).Wait(3000))
                        {
                            NetworkStream prober = probe.GetStream();
                            byte[] question = Encoding.UTF8.GetBytes(
                                "RESUME " + info.Length + " " + info.Name + "\n");
                            prober.Write(question, 0, question.Length);
                            prober.Flush();
                            string answer = ReadReply(probe, 2500);
                            if (answer != null && answer.StartsWith("HAVE "))
                            {
                                resumeSupported = true;
                                long already;
                                if (long.TryParse(answer.Substring(5).Trim(), out already)
                                    && already > 0 && already < info.Length)
                                    offset = already;
                            }
                        }
                    }
                }
                catch
                {
                }

                if (offset > 0)
                    Log.Write("resume " + info.Name + " from " + offset + "/" + info.Length
                        + " (" + (offset * 100 / Math.Max(1, info.Length)) + "%)");

                NetworkStream target = socket.GetStream();
                string header = "UPLOAD " + info.Length + " " + info.Name;
                if (resumeSupported)
                    header += " " + offset;
                byte[] head = Encoding.UTF8.GetBytes(header + "\n");
                target.Write(head, 0, head.Length);

                byte[] buffer = new byte[262144];
                long sent = 0;
                using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 262144, FileOptions.SequentialScan))
                {
                    if (offset > 0)
                        input.Seek(offset, SeekOrigin.Begin);
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (progress != null && progress.CancelRequested)
                        {
                            error = "已取消";
                            Log.Write("send file cancelled: " + info.Name + " at "
                                + (offset + sent) + "/" + info.Length + " bytes"
                                + "（已传部分留在 Ubuntu，重发会接着传）");
                            return false;
                        }
                        target.Write(buffer, 0, read);
                        sent += read;
                        if (progress != null)
                            progress.Report(info.Name, offset + sent, info.Length);
                    }
                }
                target.Flush();

                string reply = ReadReply(socket);
                if (reply == null)
                {
                    error = "没有收到 Ubuntu 的确认";
                    Log.Write("send file failed: " + info.Name + " " + error);
                    return false;
                }
                if (!reply.StartsWith("OK"))
                {
                    error = reply;
                    Log.Write("send file failed: " + info.Name + " " + reply);
                    return false;
                }
                // 回复形如 "OK /home/robot/Desktop/x.txt"，把真实落地路径带回去
                destination = reply.Substring(2).Trim();
                Log.Write("sent file: " + info.Name + " (" + info.Length + " bytes) -> " + destination);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Write("send file failed: " + Path.GetFileName(path) + " " + ex.Message);
                return false;
            }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(); }
                    catch { }
                }
            }
        }

        internal static string ReadReply(TcpClient socket, int timeoutMs = 15000)
        {
            try
            {
                socket.ReceiveTimeout = timeoutMs;
                NetworkStream source = socket.GetStream();
                List<byte> bytes = new List<byte>();
                byte[] chunk = new byte[256];
                while (bytes.Count < 4096)
                {
                    int count = source.Read(chunk, 0, chunk.Length);
                    if (count <= 0)
                        break;
                    bool finished = false;
                    for (int i = 0; i < count; i++)
                    {
                        if (chunk[i] == (byte)'\n')
                        {
                            finished = true;
                            break;
                        }
                        bytes.Add(chunk[i]);
                    }
                    if (finished)
                        break;
                }
                if (bytes.Count == 0)
                    return null;
                return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            }
            catch
            {
                return null;
            }
        }
    }

}
