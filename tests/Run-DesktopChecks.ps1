param([Parameter(Mandatory=$true)][string]$Executable, [string]$Argument = '--runtime')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class PrivateDesktopRunner {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startup {
        public int size;
        public string reserved, desktop, title;
        public int x, y, width, height, charsX, charsY, fill, flags;
        public short show, reservedBytes;
        public IntPtr reservedPointer, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr process, thread; public int processId, threadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inherit, uint flags, IntPtr environment, string directory, ref Startup startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static int Run(string executable, string argument) {
        string name = "TypeBridge-Test-" + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception();
        ProcessInfo info = new ProcessInfo();
        try {
            Startup startup = new Startup();
            startup.size = Marshal.SizeOf(typeof(Startup));
            startup.desktop = name;
            startup.flags = 0x100;
            startup.input = GetStdHandle(-10); startup.output = GetStdHandle(-11); startup.error = GetStdHandle(-12);
            StringBuilder command = new StringBuilder("\"" + executable + "\" " + argument);
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0, IntPtr.Zero,
                System.IO.Path.GetDirectoryName(executable), ref startup, out info)) throw new Win32Exception();
            if (WaitForSingleObject(info.process, 55000) != 0) {
                TerminateProcess(info.process, 1);
                throw new TimeoutException("Runtime check exceeded 55 seconds");
            }
            uint result;
            GetExitCodeProcess(info.process, out result);
            return (int)result;
        } finally {
            if (info.thread != IntPtr.Zero) CloseHandle(info.thread);
            if (info.process != IntPtr.Zero) CloseHandle(info.process);
            CloseDesktop(desktop);
        }
    }
}
'@
$env:TYPEBRIDGE_TEST_DESKTOP = '1'
try { exit [PrivateDesktopRunner]::Run((Resolve-Path $Executable).Path, $Argument) }
finally { Remove-Item Env:\TYPEBRIDGE_TEST_DESKTOP }
