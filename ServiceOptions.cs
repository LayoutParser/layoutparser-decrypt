using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LayoutParserDecrypt
{
    /// <summary>
    /// Configuração do serviço. Fonte: variáveis de ambiente (para um serviço Windows elas ficam em
    /// HKLM\SYSTEM\CurrentControlSet\Services\&lt;nome&gt;\Environment, gravadas por install-service.ps1).
    /// Valores inválidos ou ausentes caem no default — o serviço nunca deve deixar de subir por typo.
    /// </summary>
    public class ServiceOptions
    {
        /// <summary>Hosts do prefixo HttpListener: "localhost", um IP, ou "+" (todas as interfaces).</summary>
        public IList<string> BindAddresses { get; set; } = new List<string> { "localhost" };
        public int Port { get; set; } = 5220;
        public string LogDir { get; set; }

        /// <summary>Tamanho máximo do corpo em bytes → 413.</summary>
        public long MaxBodyBytes { get; set; } = 10L * 1024 * 1024;
        /// <summary>Tempo máximo de uma requisição /decrypt (leitura + descriptografia) → 504.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>Decriptações simultâneas; acima disso a requisição espera <see cref="QueueWait"/> e recebe 503.</summary>
        public int MaxConcurrency { get; set; } = 4;
        public TimeSpan QueueWait { get; set; } = TimeSpan.FromSeconds(2);

        public bool IsLoopbackOnly
        {
            get
            {
                return BindAddresses.All(a => a.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || a == "127.0.0.1" || a == "[::1]");
            }
        }

        public static ServiceOptions FromEnvironment(Func<string, string> get = null)
        {
            get = get ?? Environment.GetEnvironmentVariable;
            var o = new ServiceOptions();

            var bind = get("LAYOUTPARSER_DECRYPT_BIND");
            if (!string.IsNullOrWhiteSpace(bind))
            {
                var list = bind.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                if (list.Count > 0) o.BindAddresses = list;
            }

            o.Port = Int(get("LAYOUTPARSER_DECRYPT_PORT"), o.Port, 1, 65535);
            o.MaxBodyBytes = Int(get("LAYOUTPARSER_DECRYPT_MAX_BODY_BYTES"), (int)o.MaxBodyBytes, 1, int.MaxValue);
            o.RequestTimeout = TimeSpan.FromSeconds(Int(get("LAYOUTPARSER_DECRYPT_TIMEOUT_SECONDS"), (int)o.RequestTimeout.TotalSeconds, 1, 3600));
            o.MaxConcurrency = Int(get("LAYOUTPARSER_DECRYPT_MAX_CONCURRENCY"), o.MaxConcurrency, 1, 256);
            o.QueueWait = TimeSpan.FromMilliseconds(Int(get("LAYOUTPARSER_DECRYPT_QUEUE_WAIT_MS"), (int)o.QueueWait.TotalMilliseconds, 0, 600000));

            o.LogDir = get("LAYOUTPARSER_LOG_DIR");
            if (string.IsNullOrWhiteSpace(o.LogDir))
                o.LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            return o;
        }

        public IEnumerable<string> Prefixes()
        {
            return BindAddresses.Select(h => string.Format("http://{0}:{1}/", h, Port));
        }

        private static int Int(string raw, int fallback, int min, int max)
        {
            int v;
            return int.TryParse(raw, out v) && v >= min && v <= max ? v : fallback;
        }
    }
}
