using Microsoft.Win32;
using SaveCloud.Core;
using System;
using System.Windows.Forms;

namespace SaveCloud.Platform
{
    /// <summary>Starts SaveCloud at sign-in via the per-user Run key (no admin needed).</summary>
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SaveCloud";

        public static void Apply(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled) key.SetValue(ValueName, $"\"{Application.ExecutablePath}\" --background");
                    else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Could not update the Windows startup entry", ex);
            }
        }
    }
}
