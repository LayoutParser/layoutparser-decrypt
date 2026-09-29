using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace LayoutParserDecrypt
{
    /// <summary>
    /// Entrypoint. Rodando sob o SCM (não interativo) executa como serviço Windows; em um console
    /// (ou com <c>--console</c>) executa em primeiro plano, útil para depuração. Opcionalmente a
    /// porta pode vir como primeiro argumento numérico (compat. com a versão anterior).
    ///
    /// Contrato HTTP (congelado):
    ///   POST /decrypt   Header X-Correlation-ID (opcional) · Body texto cifrado · 200/400/422
    ///                   (+ 413 corpo grande, 503 ocupado, 504 timeout)
    ///   GET  /health    200 {"status":"ok",...} · 503 se o self-test de descriptografia falhar
    /// Configuração: ver <see cref="ServiceOptions"/> (variáveis LAYOUTPARSER_DECRYPT_*).
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            var console = Environment.UserInteractive || Array.IndexOf(args, "--console") >= 0;
            if (!console)
            {
                ServiceBase.Run(new DecryptWindowsService());
                return 0;
            }

            var options = ServiceOptions.FromEnvironment();
            if (args.Length >= 1 && int.TryParse(args[0], out var port)) options.Port = port;

            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                try
                {
                    RunServerAsync(options, cts.Token).GetAwaiter().GetResult();
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Falha ao iniciar: " + ex.Message);
                    return 1;
                }
            }
        }

        // Mantido público para testes de integração (listener real em porta efêmera).
        public static Task RunServerAsync(int port, string logDir, CancellationToken cancellationToken)
        {
            return RunServerAsync(new ServiceOptions { Port = port, LogDir = logDir }, cancellationToken);
        }

        public static async Task RunServerAsync(ServiceOptions options, CancellationToken cancellationToken)
        {
            var server = new DecryptServer(options);
            server.Start();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { }
            finally { server.Stop(); }
        }
    }
}
