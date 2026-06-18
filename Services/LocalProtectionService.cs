using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace NouzertGames.Services
{
    /// <summary>
    /// Protects local activation data with Windows DPAPI and binds it to this computer.
    /// </summary>
    public class LocalProtectionService
    {
        private const int CryptProtectLocalMachine = 0x4;
        private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("NouzertGames.LocalActivation.v1");

        public string Protect(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var payload = $"{GetMachineFingerprint()}|{value}";
            var protectedBytes = ProtectBytes(Encoding.UTF8.GetBytes(payload));
            return Convert.ToBase64String(protectedBytes);
        }

        public bool TryUnprotect(string? protectedValue, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(protectedValue))
                return false;

            try
            {
                var payload = Encoding.UTF8.GetString(UnprotectBytes(Convert.FromBase64String(protectedValue)));
                var separatorIndex = payload.IndexOf('|');
                if (separatorIndex <= 0)
                    return false;

                var fingerprint = payload[..separatorIndex];
                if (!string.Equals(fingerprint, GetMachineFingerprint(), StringComparison.Ordinal))
                    return false;

                value = payload[(separatorIndex + 1)..];
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }

        private static string GetMachineFingerprint()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var machineGuid = key?.GetValue("MachineGuid")?.ToString() ?? Environment.MachineName;
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(machineGuid));
            return Convert.ToHexString(bytes);
        }

        private static byte[] ProtectBytes(byte[] value)
        {
            return Transform(value, CryptProtectData);
        }

        private static byte[] UnprotectBytes(byte[] value)
        {
            return Transform(value, CryptUnprotectData);
        }

        private static byte[] Transform(byte[] value, CryptTransform transform)
        {
            var input = CreateBlob(value);
            var entropy = CreateBlob(OptionalEntropy);
            DATA_BLOB output = default;

            try
            {
                if (!transform(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectLocalMachine, ref output))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                if (input.pbData != IntPtr.Zero)
                    Marshal.FreeHGlobal(input.pbData);
                if (entropy.pbData != IntPtr.Zero)
                    Marshal.FreeHGlobal(entropy.pbData);
                if (output.pbData != IntPtr.Zero)
                    LocalFree(output.pbData);
            }
        }

        private static DATA_BLOB CreateBlob(byte[] value)
        {
            var blob = new DATA_BLOB
            {
                cbData = value.Length,
                pbData = Marshal.AllocHGlobal(value.Length)
            };
            Marshal.Copy(value, 0, blob.pbData, value.Length);
            return blob;
        }

        private delegate bool CryptTransform(
            ref DATA_BLOB dataIn,
            string? description,
            ref DATA_BLOB optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            ref DATA_BLOB dataOut);

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB dataIn,
            string? description,
            ref DATA_BLOB optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            ref DATA_BLOB dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB dataIn,
            string? description,
            ref DATA_BLOB optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            ref DATA_BLOB dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
