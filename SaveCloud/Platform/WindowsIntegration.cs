using Microsoft.Win32;
using SaveCloud.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace SaveCloud.Platform
{
    /// <summary>Encrypts secrets for the current Windows user (DPAPI), so config.json never holds a plain password.</summary>
    public static class Secrets
    {
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        public static string Unprotect(string protectedValue)
        {
            if (string.IsNullOrEmpty(protectedValue)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
            {
                Logger.Warn("A stored secret could not be decrypted (config copied from another PC/user?); please re-enter it in Settings");
                return null;
            }
        }
    }

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

    /// <summary>Generates an SSH key with the ssh-keygen that ships with Windows 10/11 (OpenSSH Client feature).</summary>
    public static class KeyGenerator
    {
        public static string FindSshKeygen()
        {
            string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh-keygen.exe");
            if (File.Exists(sys)) return sys;
            // 32-bit process on 64-bit Windows sees SysWOW64 instead of System32.
            string native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "OpenSSH", "ssh-keygen.exe");
            return File.Exists(native) ? native : null;
        }

        /// <summary>Creates an ed25519 key pair (no passphrase; the file is only readable by this Windows user) and returns the public key line.</summary>
        public static string Generate(string privateKeyPath)
        {
            string exe = FindSshKeygen() ?? throw new FileNotFoundException(
                "ssh-keygen was not found. Enable 'OpenSSH Client' in Windows Settings > System > Optional features, or create a key yourself.");
            if (File.Exists(privateKeyPath)) File.Delete(privateKeyPath);
            if (File.Exists(privateKeyPath + ".pub")) File.Delete(privateKeyPath + ".pub");

            var psi = new ProcessStartInfo(exe, $"-q -t ed25519 -N \"\" -C \"savecloud@{Environment.MachineName}\" -f \"{privateKeyPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                if (p.ExitCode != 0 || !File.Exists(privateKeyPath + ".pub"))
                    throw new InvalidOperationException("ssh-keygen failed: " + err);
            }
            return File.ReadAllText(privateKeyPath + ".pub").Trim();
        }
    }
}
