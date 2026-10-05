using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AP_Atlas.Core.CheeseTracker
{
    /// <summary>
    /// Keeps the Cheese Tracker API key encrypted with Windows' data protection (DPAPI) for the current Windows account:
    /// settings.json holds only ciphertext that no other account or PC can read. Elsewhere the key isn't stored at all.
    /// </summary>
    public static class CheeseKeyStore
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private const int UiForbidden = 0x1;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AP Atlas / Cheese Tracker API key");

        public static bool Available => OperatingSystem.IsWindows();

        /// <summary>The key encrypted for this Windows account (base64), or null if it couldn't be.</summary>
        public static string Protect(string secret)
        {
            if (!Available || string.IsNullOrEmpty(secret)) return null;
            var output = Transform(Encoding.UTF8.GetBytes(secret), protect: true);
            return output == null ? null : Convert.ToBase64String(output);
        }

        /// <summary>The key, or null if there's none or it can't be decrypted here (another account or PC).</summary>
        public static string Unprotect(string stored)
        {
            if (!Available || string.IsNullOrEmpty(stored)) return null;
            byte[] input;
            try { input = Convert.FromBase64String(stored); }
            catch (FormatException) { return null; }
            var output = Transform(input, protect: false);
            return output == null ? null : Encoding.UTF8.GetString(output);
        }

        private static byte[] Transform(byte[] input, bool protect)
        {
            var inBlob = new DataBlob();
            var entropyBlob = new DataBlob();
            var outBlob = new DataBlob();
            try
            {
                inBlob = ToBlob(input);
                entropyBlob = ToBlob(Entropy);
                bool ok = protect
                    ? CryptProtectData(ref inBlob, "AP Atlas", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob);
                if (!ok || outBlob.Data == IntPtr.Zero) return null;
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (inBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.Data);
                if (entropyBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlob.Data);
                if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
            }
        }

        private static DataBlob ToBlob(byte[] bytes)
        {
            var blob = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }
    }
}
