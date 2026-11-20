using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace TypeBridgeApp
{
    // Replay real hook callbacks without installing a hook or capturing user input.
    internal static class EdgeSwitchChecks
    {
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object target, string name, object value)
        { target.GetType().GetField(name, Private).SetValue(target, value); }
        private static object Get(object target, string name)
        { return target.GetType().GetField(name, Private).GetValue(target); }
        private static void Check(bool value, string message)
        { if (!value) throw new Exception(message); }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseData
        {
            public int X, Y;
            public uint MouseDataRaw, Flags, Time;
            public UIntPtr ExtraInfo;
        }

        private static void Move(GlobalMouseHook hook, int dx, int dy)
        {
            MouseData data = new MouseData();
            data.X = (int)Get(hook, "anchorX") + dx;
            data.Y = (int)Get(hook, "anchorY") + dy;
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(data));
            try
            {
                Marshal.StructureToPtr(data, pointer, false);
                typeof(GlobalMouseHook).GetMethod("MouseCallback", Private)
                    .Invoke(hook, new object[] { 0, (IntPtr)0x0200, pointer });
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        internal static void Run()
        {
            if (Environment.GetEnvironmentVariable("TYPEBRIDGE_TEST_DESKTOP") != "1")
                throw new InvalidOperationException("Use Run-DesktopChecks.ps1 with --edge-runtime");
            int failures = 0;
            foreach (string edge in new string[] { "right", "left", "top", "bottom" })
            {
                using (GlobalMouseHook hook = new GlobalMouseHook())
                {
                    try
                    {
                        hook.SwitchEdge = edge;
                        hook.RemoteWidth = 1280;
                        hook.RemoteHeight = 800;
                        // Activate only the callback path; no OS hook, timer or cursor hiding.
                        Set(hook, "hook", (IntPtr)1);
                        Set(hook, "enabled", true);
                        Set(hook, "connected", true);
                        int returns = 0;
                        hook.EdgeReturned += delegate { returns++; };
                        int x = edge == "right" ? 48 : edge == "left" ? 1231 : 640;
                        int y = edge == "bottom" ? 48 : edge == "top" ? 751 : 400;
                        int dx = edge == "right" ? -10 : edge == "left" ? 10 : 0;
                        int dy = edge == "bottom" ? -10 : edge == "top" ? 10 : 0;
                        hook.BeginTracking(x, y);
                        Set(hook, "trackingStartedTick", unchecked(Environment.TickCount + 1000));
                        Move(hook, dx * 6, dy * 6);
                        Check(returns == 0, edge + ": entry jitter must not bounce back immediately");
                        // Complete repeated short tasks at the entry edge. Freeze elapsed time
                        // instead of sleeping, using wrap-safe TickCount arithmetic.
                        for (int trip = 0; trip < 25; trip++)
                        {
                            hook.BeginTracking(x, y);
                            Set(hook, "trackingStartedTick", unchecked(Environment.TickCount - 350));
                            for (int i = 0; i < 12; i++) Move(hook, dx, dy);
                            Check(returns == trip + 1, edge + ": outward movement must return exactly once; actual=" + returns);
                            hook.StopTracking();
                        }
                        Console.WriteLine("PASS short round trip " + edge);
                    }
                    catch (Exception error) { failures++; Console.WriteLine("FAIL " + error.Message); }
                    finally { Set(hook, "hook", IntPtr.Zero); Set(hook, "enabled", false); }
                }
            }
            if (failures != 0) throw new Exception(failures + " edge regressions");
        }

        internal static void ReverseChecks()
        {
            int failures = 0;
            foreach (string edge in new string[] { "right", "left", "top", "bottom" })
            {
                foreach (bool fast in new bool[] { false, true })
                {
                    // No application constructor: this decision path needs no desktop, socket or hooks.
                    object context = FormatterServices.GetUninitializedObject(typeof(BridgeApplicationContext));
                    DateTime started = new DateTime(2026, 1, 1);
                    Set(context, "edgeSwitch", true);
                    Set(context, "switchEdge", edge);
                    Set(context, "remoteDriveSince", started);
                    Rectangle bounds = new Rectangle(0, 0, 1280, 800);
                    Point atEdge = edge == "right" ? new Point(1279, 400) : edge == "left" ? new Point(0, 400)
                        : edge == "top" ? new Point(640, 0) : new Point(640, 799);
                    MethodInfo decision = typeof(BridgeApplicationContext).GetMethod("ShouldReturnRemoteMouse", Private);
                    if (fast)
                    {
                        // Arm using the normal inside-screen path, then return with a fast stroke.
                        decision.Invoke(context, new object[] { 1, 0, bounds, new Point(640, 400), started.AddSeconds(1) });
                    }
                    int step = fast ? 90 : 10;
                    int dx = edge == "right" ? step : edge == "left" ? -step : 0;
                    int dy = edge == "bottom" ? step : edge == "top" ? -step : 0;
                    Check(!(bool)decision.Invoke(context, new object[] { dx, dy, bounds, atEdge, started.AddMilliseconds(100) }),
                        "reverse entry jitter must not bounce back");
                    Check(!(bool)decision.Invoke(context, new object[] { 0, 0, bounds, atEdge, started.AddSeconds(2) }),
                        "resting on the edge must not return");
                    Check(!(bool)decision.Invoke(context, new object[] { -dx, -dy, bounds, atEdge, started.AddSeconds(2) }),
                        "moving inside must not return");
                    Check(!(bool)decision.Invoke(context, new object[] { dx, dy, bounds, new Point(640, 400), started.AddSeconds(2) }),
                        "moving away from the edge must not return");
                    bool returned = false;
                    for (int i = 0; i < 10; i++)
                        returned |= (bool)decision.Invoke(context, new object[] { dx, dy, bounds, atEdge, started.AddSeconds(2) });
                    if (!returned) { failures++; Console.WriteLine("FAIL reverse " + edge + (fast ? " fast push" : " short round trip")); }
                    Set(context, "edgeSwitch", false);
                    Check(!(bool)decision.Invoke(context, new object[] { dx, dy, bounds, atEdge, started.AddSeconds(2) }),
                        "edge switch disabled must be respected");
                }
            }
            if (failures != 0) throw new Exception(failures + " reverse edge regressions");
            Console.WriteLine("PASS reverse short round trips and fast pushes in four directions");
        }
    }
}
