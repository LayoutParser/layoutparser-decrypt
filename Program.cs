using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LayoutParserLib;

namespace LayoutParserDecrypt
{
    /// <summary>
    /// Serviço HTTP auto-hospedado (ADR segregação Decrypt/LowCodeRunner, 2026-09-25), substituindo
    /// o CLI antigo (<c>&lt;inputFile&gt; &lt;outputFile&gt; [correlationId] [logDir]</c>).
    ///
    /// Usa <see cref="HttpListener"/> em vez de ASP.NET Core porque a criptografia precisa continuar
    /// rodando em .NET Framework 4.8.1 (ver comentário no .csproj — RijndaelManaged com chave/IV de
    /// tamanho não-AES não funciona no shim de .NET moderno) e ASP.NET Core Kestrel não roda em net48.
    ///
    /// Contrato:
    ///   POST /decrypt   Header X-Correlation-ID (opcional) · Body texto cifrado · 200/400/422
    ///   GET  /health    200 {"status":"ok"}
    /// </summary>
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var port = GetPort(args);
            var logDir = Environment.GetEnvironmentVariable("LAYOUTPARSER_LOG_DIR")
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

                await RunServerAsync(port, logDir, cts.Token);
            }
        }

        private static int GetPort(string[] args)
        {
            if (args != null && args.Length >= 1 && int.TryParse(args[0], out var argPort))
                return argPort;

            var envPort = Environment.GetEnvironmentVariable("LAYOUTPARSER_DECRYPT_PORT");
            if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out var parsedPort))
                return parsedPort;

            return 5220;
        }

        // public para permitir teste de integração real (HttpListener numa porta efêmera) sem
        // precisar subir o processo Main inteiro — mesmo padrão de composition-root testing já
        // usado em LayoutParserApi.
        public static async Task RunServerAsync(int port, string logDir, CancellationToken cancellationToken)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(string.Format("http://localhost:{0}/", port));
            listener.Start();

            RollingFileLogger.Configure(logDir, "N/A");
            RollingFileLogger.Log("INF", string.Format("LayoutParserDecrypt HTTP service iniciado na porta {0}", port));

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        var getContextTask = listener.GetContextAsync();
                        var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
                        var winner = await Task.WhenAny(getContextTask, cancelTask);
                        if (winner != getContextTask) break;
                        context = await getContextTask;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }

                    // Fire-and-forget por request — não bloqueia o accept loop enquanto processa.
                    _ = Task.Run(() => HandleRequestAsync(context, logDir));
                }
            }
            finally
            {
                listener.Stop();
                listener.Close();
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context, string logDir)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                var correlationId = request.Headers["X-Correlation-ID"];
                if (string.IsNullOrWhiteSpace(correlationId))
                    correlationId = Guid.NewGuid().ToString();

                RollingFileLogger.Configure(logDir, correlationId);

                if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/health")
                {
                    await WriteResponseAsync(response, 200, "application/json", "{\"status\":\"ok\"}");
                    return;
                }

                if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/decrypt")
                {
                    RollingFileLogger.Log("INF", "START /decrypt");
                    string body;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        body = await reader.ReadToEndAsync();

                    var result = RequestHandler.HandleDecrypt(correlationId, body,
                        (level, message, ex) => RollingFileLogger.Log(level, message, ex));

                    await WriteResponseAsync(response, result.StatusCode, result.ContentType, result.Body);
                    return;
                }

                await WriteResponseAsync(response, 404, "application/json", "{\"error\":\"not found\"}");
            }
            catch (Exception ex)
            {
                try
                {
                    RollingFileLogger.Log("ERR", "Erro não tratado no request handler", ex);
                    await WriteResponseAsync(response, 500, "application/json", "{\"error\":\"internal error\"}");
                }
                catch { }
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        private static async Task WriteResponseAsync(HttpListenerResponse response, int statusCode, string contentType, string body)
        {
            response.StatusCode = statusCode;
            response.ContentType = contentType + "; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }
    }
}
