using System;
using System.Diagnostics;
using System.Security.Principal;

namespace TypeBridgeApp
{
    internal static class AdministratorMode
    {
        internal static bool IsEnabled
        {
            get
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        internal static ProcessStartInfo RestartInfo(string executable)
        {
            return new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = System.IO.Path.GetDirectoryName(executable),
                Arguments = "--wait-for-exit",
                UseShellExecute = true,
                Verb = "runas"
            };
        }
    }
}
