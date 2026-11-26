using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TypeBridgeApp
{
    internal static class ClipboardAndTakeoverChecks
    {
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseData
        {
            public int X, Y;
            public uint Data, Flags, Time;
            public UIntPtr Extra;
        }

        private static void Replay(GlobalMouseHook hook, int message, Point point, uint flags)
        {
            MouseData data = new MouseData { X = point.X, Y = point.Y, Flags = flags };
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(data));
            try
            {
                Marshal.StructureToPtr(data, ptr, false);
                typeof(GlobalMouseHook).GetField("lastLocalInputTick", Private)
                    .SetValue(hook, unchecked(Environment.TickCount - 1000));
                typeof(GlobalMouseHook).GetMethod("MouseCallback", Private)
                    .Invoke(hook, new object[] { 0, (IntPtr)message, ptr });
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        internal static void Run()
        {
            if (Environment.GetEnvironmentVariable("TYPEBRIDGE_TEST_DESKTOP") != "1")
                throw new InvalidOperationException("Use the private desktop runner");
            using (GlobalMouseHook hook = new GlobalMouseHook())
            {
                int takeovers = 0;
                hook.LocalWatch = true;
                hook.LocalInput += delegate { takeovers++; };
                Point start = Cursor.Position;
                Rectangle bounds = SystemInformation.VirtualScreen;
                Point target = new Point(Math.Max(bounds.Left, Math.Min(bounds.Right - 1, start.X + 23)),
                    Math.Max(bounds.Top, Math.Min(bounds.Bottom - 1, start.Y + 17)));
                RemoteInput.MoveBy(23, 17);
                // Replay the WM_MOUSEMOVE from SetCursorPos (Flags=0). No OS hook,
                // clicks or input capture: use the real movement and detector paths.
                Replay(hook, 0x0200, target, 0);
                if (takeovers != 0) throw new Exception("Remote movement was mistaken for local input; Ubuntu grab would be released");
                Replay(hook, 0x0200, new Point(target.X + 7, target.Y + 7), 0);
                if (takeovers != 1) throw new Exception("Real local movement must still take over");
                RemoteInput.MoveBy(23, 17);
                Replay(hook, 0x0201, target, 0);
                if (takeovers != 2) throw new Exception("A real click at a warp position must still take over");
                Replay(hook, 0x0200, target, 1);
                if (takeovers != 2) throw new Exception("Injected input must not take over");
                Console.WriteLine("PASS programmatic movement, real movement, same-position click and injected events");
            }
        }

        internal static void ClipboardRetries()
        {
            ClipboardWriteQueue queue = new ClipboardWriteQueue(delegate(string message) { });
            string clipboard = "old";
            bool busy = true;
            queue.Enqueue(delegate { if (busy) throw new ExternalException("clipboard busy"); clipboard = "Ubuntu text"; }, 10);
            if (!queue.Pending || clipboard != "old") throw new Exception("Busy clipboard must retain the incoming text");
            busy = false;
            queue.Retry(10);
            if (queue.Pending || clipboard != "Ubuntu text") throw new Exception("Text must arrive after clipboard unlocks");
            busy = true;
            queue.Enqueue(delegate { if (busy) throw new ExternalException(); clipboard = "old image"; }, 11);
            queue.Enqueue(delegate { if (busy) throw new ExternalException(); clipboard = "new image"; }, 11);
            busy = false;
            queue.Retry(11);
            if (clipboard != "new image") throw new Exception("A newer incoming image must replace an older pending image");
            busy = true;
            queue.Enqueue(delegate { if (busy) throw new ExternalException(); clipboard = "stale remote"; }, 12);
            clipboard = "new local copy";
            busy = false;
            queue.Retry(13);
            if (queue.Pending || clipboard != "new local copy") throw new Exception("Retry must not overwrite a newer local copy");
            Console.WriteLine("PASS clipboard contention recovery, latest incoming value and newer local copy protection");
        }
    }
}
