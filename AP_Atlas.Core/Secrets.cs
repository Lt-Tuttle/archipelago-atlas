#nullable disable
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AP_Atlas.Core
{
    /// <summary>
    /// Encrypts secrets (API keys, room passwords) with Windows' data protection (DPAPI) for the current Windows account:
    /// what Atlas saves is ciphertext that no other account or PC can read. Each kind of secret uses its own extra
    /// entropy, so one kind can't be swapped in for another.
    /// </summary>
    public static class Secrets
    {
        /// <summary>A kind of secret; its purpose string is mixed into the encryption.</summary>
        public sealed class Kind
        {
            internal readonly byte[] Entropy;
            internal Kind(string purpose) => Entropy = Encoding.UTF8.GetBytes(purpose);
        }

        /// <summary>The Cheese Tracker API key (this purpose string predates this class; changing it would lose saved keys).</summary>
        public static readonly Kind CheeseApiKey = new Kind("AP Atlas / Cheese Tracker API key");

        /// <summary>A multiworld's room password.</summary>
        public static readonly Kind RoomPassword = new Kind("The Archipelago Atlas / room password");

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

        public static bool Available => OperatingSystem.IsWindows();

        /// <summary>The secret encrypted for this Windows account (base64), or null if there's nothing to encrypt or it couldn't be.</summary>
        public static string Protect(string secret, Kind kind)
        {
            if (!Available || string.IsNullOrEmpty(secret)) return null;
            var output = Transform(Encoding.UTF8.GetBytes(secret), kind.Entropy, protect: true);
            return output == null ? null : Convert.ToBase64String(output);
        }

        /// <summary>The secret, or null if there's none or it can't be decrypted here (another Windows account or PC).</summary>
        public static string Unprotect(string stored, Kind kind)
        {
            if (!Available || string.IsNullOrEmpty(stored)) return null;
            byte[] input;
            try { input = Convert.FromBase64String(stored); }
            catch (FormatException) { return null; }
            var output = Transform(input, kind.Entropy, protect: false);
            return output == null ? null : Encoding.UTF8.GetString(output);
        }

        private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
        {
            var inBlob = new DataBlob();
            var entropyBlob = new DataBlob();
            var outBlob = new DataBlob();
            try
            {
                inBlob = ToBlob(input);
                entropyBlob = ToBlob(entropy);
                bool ok = protect
                    ? CryptProtectData(ref inBlob, "The Archipelago Atlas", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob)
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
