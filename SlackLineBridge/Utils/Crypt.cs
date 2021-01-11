using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SlackLineBridge.Utils
{
    public class Crypt
    {
        public static byte[] CalcHMAC(string text, string key)
        {
            var encoding = new UTF8Encoding();

            var textBytes = encoding.GetBytes(text);
            var keyBytes = encoding.GetBytes(key);

            using var hash = new HMACSHA256(keyBytes);
            return hash.ComputeHash(textBytes);
        }

        public static string GetHMACBase64(string text, string key) => Convert.ToBase64String(CalcHMAC(text, key));

        public static string GetHMACHex(string text, string key) => BitConverter.ToString(CalcHMAC(text, key)).Replace("-", "").ToLower();
    }
}
