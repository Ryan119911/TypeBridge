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
        // ---------- 剪贴板 ----------

        private void PollClipboard()
        {
            if (stream == null || disposed)
                return;

            // 剪贴板没变就不必每 600ms 把整张位图重新编码成 PNG（4K 截图会明显卡顿）。
            uint seq = GetClipboardSequenceNumber();
            if (seq == lastClipboardSeq)
                return;
            lastClipboardSeq = seq;

            // 远端刚写进来的图文：无论回读能否解码成位图都要消费掉回声标志，
            // 否则标志会残留到下一轮，把用户自己复制的新图当成回声吞掉。
            if (suppressImageEcho)
            {
                suppressImageEcho = false;
                try
                {
                    string echoedText = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                    byte[] echoed = ClipboardImageToPng();
                    if (echoed != null)
                        lastLocalImageKey = ImageKey(echoed, echoedText);
                }
                catch { }
                return;
            }

            string text = string.Empty;
            byte[] image = null;
            try
            {
                // 图文一起同步：浏览器"复制图片"、Excel 复制区域都是位图 + 文本，
                // 只发其中一种，另一端就粘不出用户真正想要的东西。
                if (Clipboard.ContainsText())
                    text = Clipboard.GetText();
                image = ClipboardImageToPng();
            }
            catch
            {
                return;
            }

            if (image != null)
            {
                string key = ImageKey(image, text);
                if (key == lastLocalImageKey)
                    return;
                lastLocalImageKey = key;
                lastLocalClip = text;
                Log.Write("clipboard image -> ubuntu (" + image.Length + " bytes, text "
                    + text.Length + " chars)");
                byte[] payload = image;
                string caption = text;
                System.Threading.ThreadPool.QueueUserWorkItem(
                    delegate { UploadClipboardImage(payload, caption); });
                return;
            }

            if (text == lastLocalClip)
                return;
            lastLocalClip = text;
            if (text.Length == 0 || text == lastRemoteClip)
                return;
            lastLocalImageKey = null;
            SendLine("C " + json.Serialize(text));
        }

        /// <summary>优先取 CF_DIB/位图；有些程序只往剪贴板放注册格式 PNG。</summary>
        private static byte[] ClipboardImageToPng()
        {
            try
            {
                using (Image image = Clipboard.GetImage())
                {
                    if (image != null)
                    {
                        using (MemoryStream buffer = new MemoryStream())
                        {
                            image.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
                            return buffer.ToArray();
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data != null && data.GetDataPresent("PNG"))
                {
                    MemoryStream stream = data.GetData("PNG") as MemoryStream;
                    if (stream != null)
                        return stream.ToArray();
                    byte[] raw = data.GetData("PNG") as byte[];
                    if (raw != null)
                        return raw;
                }
            }
            catch
            {
            }
            return null;
        }

        private static string ImageKey(byte[] png, string text)
        {
            return Hash(png) + "#" + Hash(Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        private static string Hash(byte[] data)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(data);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>
        /// 把剪贴板图片（以及可选的文本）按 CLIPIMG 协议推给 Ubuntu 的文件端口，不落盘。
        /// 报文：CLIPIMG &lt;图片字节数&gt; &lt;文本字节数&gt;\n + PNG + 文本。
        /// 文本放在后面，旧版守护进程只读图片部分也能正常工作。
        /// </summary>
        private void UploadClipboardImage(byte[] png, string text)
        {
            if (png == null || png.Length == 0)
                return;
            if (png.Length > MaxClipImageBytes)
            {
                Log.Write("clipboard image too large, skipped: " + png.Length + " bytes");
                return;
            }
            if (System.Threading.Interlocked.CompareExchange(ref imageSending, 1, 0) != 0)
                return;   // 上一张还在传，这张直接跳过，避免堆积

            byte[] caption = Encoding.UTF8.GetBytes(text ?? string.Empty);
            TcpClient socket = null;
            try
            {
                socket = new TcpClient();
                socket.NoDelay = true;
                string[] fileHosts = FileTargetHosts();
                if (!socket.ConnectAsync(fileHosts[0], filePort).Wait(4000))
                {
                    Log.Write("clipboard image: 连不上文件端口 " + filePort);
                    return;
                }

                NetworkStream target = socket.GetStream();
                byte[] head = Encoding.UTF8.GetBytes("CLIPIMG " + png.Length + " " + caption.Length + "\n");
                target.Write(head, 0, head.Length);
                target.Write(png, 0, png.Length);
                if (caption.Length > 0)
                    target.Write(caption, 0, caption.Length);
                target.Flush();

                string reply = ReadReply(socket);
                Log.Write("clipboard image -> ubuntu: " + (reply ?? "(no reply)"));
            }
            catch (Exception ex)
            {
                Log.Write("clipboard image upload failed: " + ex.Message);
            }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(); }
                    catch { }
                }
                System.Threading.Interlocked.Exchange(ref imageSending, 0);
            }
        }

        private void ApplyRemoteClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            lastRemoteClip = text;
            lastLocalClip = text;
            lastLocalImageKey = null;
            try { Clipboard.SetText(text); }
            catch { }
        }

        private void ApplyRemoteClipboardImage(byte[] png, string text)
        {
            if (png == null || png.Length == 0)
                return;
            try
            {
                // DataObject 会持有 Bitmap 供剪贴板按需序列化，成功时不能 Dispose；
                // 但 SetDataObject 抛异常（剪贴板被别的程序占用很常见）时要释放，
                // 否则每失败一次就泄漏一个 GDI+ 句柄。
                Bitmap image = null;
                bool handedOff = false;
                try
                {
                    using (MemoryStream buffer = new MemoryStream(png))
                    using (Image loaded = Image.FromStream(buffer))
                        image = new Bitmap(loaded);

                    // 图 + 文一起放：粘到图片程序里是图，粘到输入框里是文本
                    DataObject payload = new DataObject();
                    payload.SetData(DataFormats.Bitmap, true, image);
                    if (!string.IsNullOrEmpty(text))
                        payload.SetData(DataFormats.UnicodeText, true, text);
                    Clipboard.SetDataObject(payload, true);
                    handedOff = true;
                }
                finally
                {
                    if (!handedOff && image != null)
                        image.Dispose();
                }

                suppressImageEcho = true;
                lastLocalImageKey = null;
                lastLocalClip = text ?? string.Empty;
                lastRemoteClip = text ?? string.Empty;
                Log.Write("clipboard <- ubuntu image (" + png.Length + " bytes, text "
                    + (text == null ? 0 : text.Length) + " chars)");
            }
            catch (Exception ex)
            {
                Log.Write("set clipboard image failed: " + ex.Message);
            }
        }

    }
}
