using System;
using System.Security.Cryptography;
using System.Text;

namespace SlackLineBridge.Utils
{
    public class Crypt
    {
        public static byte[] CalcHMAC(string text, string key)
        {
            var encoding = new UTF8Encoding();
            return CalcHMAC(encoding.GetBytes(text), encoding.GetBytes(key));
        }

        public static byte[] CalcHMAC(byte[] textBytes, string key)
        {
            return CalcHMAC(textBytes, new UTF8Encoding().GetBytes(key));
        }

        private static byte[] CalcHMAC(byte[] textBytes, byte[] keyBytes)
        {
            using var hash = new HMACSHA256(keyBytes);
            return hash.ComputeHash(textBytes);
        }

        public static string GetHMACBase64(string text, string key) => Convert.ToBase64String(CalcHMAC(text, key));

        public static string GetHMACHex(string text, string key) => Convert.ToHexString(CalcHMAC(text, key)).ToLowerInvariant();

        public static string GetHMACHex(byte[] textBytes, string key) => Convert.ToHexString(CalcHMAC(textBytes, key)).ToLowerInvariant();

        public static bool FixedTimeEqualsAscii(string expected, string provided)
        {
            if (expected == null || provided == null || expected.Length != provided.Length)
            {
                return false;
            }

            var expectedBytes = new byte[expected.Length];
            var providedBytes = new byte[provided.Length];
            for (var index = 0; index < expected.Length; index++)
            {
                if (expected[index] > 0x7F || provided[index] > 0x7F)
                {
                    return false;
                }

                expectedBytes[index] = (byte)expected[index];
                providedBytes[index] = (byte)provided[index];
            }

            return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
        }
    }
}
