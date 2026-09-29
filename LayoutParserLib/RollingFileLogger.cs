using System;
using System.IO;
using System.Text;
using System.Threading;

namespace LayoutParserLib
{
    /// <summary>
    /// Logger de arquivo rotativo usado pela camada de criptografia (<see cref="CryptographySysMiddle"/>).
    ///
    /// Migrado de CLI para serviço HTTP (ADR segregação Decrypt/LowCodeRunner, 2026-09-25): o diretório
    /// de log é configurado uma única vez na subida do serviço, mas o CorrelationId agora é por
    /// request — usa <see cref="AsyncLocal{T}"/> em vez de campo estático simples, para não vazar o
    /// correlationId de uma requisição concorrente para o log de outra.
    /// </summary>
    internal static class RollingFileLogger
    {
        private const long MaxBytes = 2049L * 1024L;
        private const int MaxFiles = 10;

        private static string _logDir = "";
        private static readonly AsyncLocal<string> _corr = new AsyncLocal<string>();

        internal static void Configure(string logDir, string correlationId)
        {
            _logDir = logDir ?? "";
            _corr.Value = correlationId ?? "";
        }

        internal static void Log(string level, string message, Exception ex = null)
        {
            try
            {
                var logDir = _logDir;
                var corr = _corr.Value;
                if (string.IsNullOrWhiteSpace(corr)) corr = "N/A";

                if (string.IsNullOrWhiteSpace(logDir))
                    logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

                Directory.CreateDirectory(logDir);
                var baseFileName = "layoutparserlib.log";
                var basePath = Path.Combine(logDir, baseFileName);
                RollIfNeeded(logDir, baseFileName, basePath);

                var line = $"{DateTime.UtcNow:O} [{level}] [Corr:{corr}] {message}";
                if (ex != null) line += " | " + ex;
                File.AppendAllText(basePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private static void RollIfNeeded(string logDir, string baseFileName, string basePath)
        {
            try
            {
                var fi = new FileInfo(basePath);
                if (!fi.Exists) return;
                if (fi.Length < MaxBytes) return;

                var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss-fff");
                var rolled = Path.Combine(logDir, Path.GetFileNameWithoutExtension(baseFileName) + "-" + stamp + Path.GetExtension(baseFileName));
                File.Move(basePath, rolled);

                var pattern = Path.GetFileNameWithoutExtension(baseFileName) + "-*.log";
                var files = new DirectoryInfo(logDir).GetFiles(pattern);
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = MaxFiles - 1; i < files.Length; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }
    }
}
