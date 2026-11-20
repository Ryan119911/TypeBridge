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
    internal static class Log
    {
        private const long MaxBytes = 256 * 1024;
        private static readonly object Gate = new object();
        private static readonly Queue<string> Pending = new Queue<string>();
        private static System.Threading.Thread writer;
        private static bool pathBroken;

        /// <summary>
        /// 只入队，立即返回。钩子回调里绝不能碰磁盘——一次 fsync 抖动就够让
        /// 低级钩子超时被系统摘掉，那正是"切不回 Windows"的另一条死路。
        /// </summary>
        internal static void Write(string message)
        {
            if (pathBroken)
                return;
            try
            {
                lock (Gate)
                {
                    if (Pending.Count > 500)
                        Pending.Clear();
                    Pending.Enqueue(DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "  " + message);
                    if (writer != null)
                        return;
                    writer = new System.Threading.Thread(WriterLoop);
                    writer.IsBackground = true;
                    writer.Name = "TypeBridge-Log";
                    writer.Start();
                }
            }
            catch
            {
                pathBroken = true;
            }
        }

        private static void WriterLoop()
        {
            while (true)
            {
                string line = null;
                lock (Gate)
                {
                    if (Pending.Count > 0)
                        line = Pending.Dequeue();
                    else
                        writer = null;
                }
                if (line == null)
                    return;
                try
                {
                    string path = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "typebridge.log");
                    try
                    {
                        System.IO.FileInfo info = new System.IO.FileInfo(path);
                        if (info.Exists && info.Length > MaxBytes)
                        {
                            System.IO.File.Copy(path, path + ".1", true);
                            System.IO.File.WriteAllText(path, string.Empty);
                        }
                    }
                    catch { }
                    System.IO.File.AppendAllText(path, line + "\r\n");
                }
                catch
                {
                    pathBroken = true;
                    return;
                }
            }
        }
    }

}
