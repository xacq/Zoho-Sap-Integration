using System;
using System.IO;
using System.Text;
using SohoSapIntegrator.Config;
using SohoSapIntegrator.Core.Interfaces;

namespace SohoSapIntegrator.Logging
{
    /// <summary>
    /// Logger que escribe simultáneamente en consola y en archivo de texto diario.
    /// Diseñado para ser thread-safe y compatible con .NET Framework 4.0.
    /// </summary>
    public class ConsoleFileLogger : ILogger
    {
        private static readonly object _lock = new object();
        private readonly string _logDir;
        private readonly LogLevel _minLevel;

        public ConsoleFileLogger()
        {
            _logDir = AppSettings.LogDirectory;

            LogLevel parsed;
            _minLevel = Enum.TryParse(AppSettings.LogLevel, true, out parsed)
                ? parsed
                : LogLevel.INFO;

            // Crear directorio de logs si no existe
            if (!Directory.Exists(_logDir))
            {
                try { Directory.CreateDirectory(_logDir); }
                catch { /* Si falla, solo se logea en consola */ }
            }
        }

        public void Debug(string message)
        {
            if (_minLevel <= LogLevel.DEBUG)
                Write("DEBUG", message, null, ConsoleColor.Gray);
        }

        public void Info(string message)
        {
            if (_minLevel <= LogLevel.INFO)
                Write("INFO ", message, null, ConsoleColor.White);
        }

        public void Warn(string message)
        {
            if (_minLevel <= LogLevel.WARN)
                Write("WARN ", message, null, ConsoleColor.Yellow);
        }

        public void Error(string message)
        {
            if (_minLevel <= LogLevel.ERROR)
                Write("ERROR", message, null, ConsoleColor.Red);
        }

        public void Error(string message, Exception ex)
        {
            if (_minLevel <= LogLevel.ERROR)
                Write("ERROR", message, ex, ConsoleColor.Red);
        }

        // ── Implementación interna ──────────────────────────────────────────────

        private void Write(string level, string message, Exception ex, ConsoleColor color)
        {
            var now = DateTime.Now;
            var line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}",
                now, level, message);

            string exLine = null;
            if (ex != null)
                exLine = string.Format("  EXCEPTION: {0}\n  STACK: {1}", ex.Message, ex.StackTrace);

            lock (_lock)
            {
                // Consola con color
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(line);
                if (exLine != null)
                    Console.WriteLine(exLine);
                Console.ForegroundColor = prev;

                // Archivo diario: SohoSapIntegrator_2026-02-18.log
                WriteToFile(now, line, exLine);
            }
        }

        private void WriteToFile(DateTime now, string line, string exLine)
        {
            if (string.IsNullOrEmpty(_logDir)) return;

            try
            {
                var filePath = Path.Combine(
                    _logDir,
                    string.Format("SohoSapIntegrator_{0:yyyy-MM-dd}.log", now));

                var sb = new StringBuilder();
                sb.AppendLine(line);
                if (exLine != null)
                    sb.AppendLine(exLine);

                File.AppendAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // No propagar errores de escritura en log para no romper el flujo principal
            }
        }

        private enum LogLevel
        {
            DEBUG = 0,
            INFO = 1,
            WARN = 2,
            ERROR = 3
        }
    }
}
