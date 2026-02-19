using System.Security.Cryptography;
using System.Text;

namespace SohoSapIntegrator.Utils
{
    /// <summary>
    /// Utilidades de hash para la idempotencia.
    /// </summary>
    public static class HashHelper
    {
        /// <summary>
        /// Calcula el hash SHA256 de una cadena y lo devuelve como hexadecimal en minúsculas.
        /// </summary>
        public static string Sha256Hex(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
