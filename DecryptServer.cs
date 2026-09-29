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
    /// Servidor HTTP (HttpListener). Contrato congelado: <c>GET /health</c> e <c>POST /decrypt</c>.
    /// Acrescenta limites (413 tamanho, 504 timeout, 503 concorrência) e self-test no /health (503 se falhar).
    /// Sem autenticação por design: a proteção é isolamento de rede (firewall restrito ao IP da API).
    /// </summary>
    public sealed class DecryptServer
    {
        // Par cifra/claro gerado com os mesmos parâmetros de CryptographySysMiddle (sem novo segredo no código).
        // Já com o prefixo de 3 chars, como o payload real.
        internal const string SelfTestPayload = "XXXiaaOXUaXEcxyAReiZ5fZJA==";
        internal const string SelfTestExpected = "LPD-SELFTEST-OK";
        private static readonly TimeSpan SelfTestCacheTtl = TimeSpan.FromSeconds(15);

        private readonly ServiceOptions _options;
        private readonly SemaphoreSlim _gate;
        private HttpListener _listener;
        private Task _loop;
        private CancellationTokenSource _cts;

        private readonly object _selfTestLock = new object();
        private DateTime _selfTestAt = DateTime.MinValue;
        private string _selfTestError;

        public DecryptServer(ServiceOptions options)
        {
            _options = options;
            _gate = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        }

        /// <summary>Abre o listener (lança se a porta/urlacl falhar) e inicia o loop de accept.</summary>
        public void Start()
        {
            RollingFileLogger.Configure(_options.LogDir, "N/A");

            var listener = new HttpListener();
            foreach (var prefix in _options.Prefixes()) listener.Prefixes.Add(prefix);
            listener.TimeoutManager.HeaderWait = TimeSpan.FromSeconds(15);
            listener.TimeoutManager.EntityBody = _options.RequestTimeout;
            listener.TimeoutManager.IdleConnection = TimeSpan.FromSeconds(60);
            listener.Start();
            _listener = listener;

            RollingFileLogger.Log("INF", string.Format(
                "LayoutParserDecrypt HTTP service iniciado em {0} (maxBody={1}B timeout={2}s concurrency={3})",
                string.Join(", ", _options.Prefixes()), _options.MaxBodyBytes,
                (int)_options.RequestTimeout.TotalSeconds, _options.MaxConcurrency));
            if (!_options.IsLoopbackOnly)
                RollingFileLogger.Log("WRN", "Bind não-loopback: sem autenticação, dependendo do firewall restrito ao IP da API.");

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_listener != null) { _listener.Stop(); _listener.Close(); } } catch { }
            try { if (_loop != null) _loop.Wait(TimeSpan.FromSeconds(10)); } catch { }
            RollingFileLogger.Log("INF", "LayoutParserDecrypt HTTP service parado");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (InvalidOperationException) { break; }

                _ = Task.Run(() => HandleRequestAsync(context));
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                var correlationId = request.Headers["X-Correlation-ID"];
                if (string.IsNullOrWhiteSpace(correlationId)) correlationId = Guid.NewGuid().ToString();
                RollingFileLogger.Configure(_options.LogDir, correlationId);

                var path = request.Url.AbsolutePath;
                if (request.HttpMethod == "GET" && path == "/health")
                {
                    if (RunSelfTest() == null)
                        await WriteAsync(response, 200, "application/json", "{\"status\":\"ok\",\"selfTest\":\"ok\"}");
                    else
                        await WriteAsync(response, 503, "application/json", "{\"status\":\"unhealthy\",\"selfTest\":\"failed\"}");
                    return;
                }

                if (request.HttpMethod == "POST" && path == "/decrypt")
                {
                    await HandleDecryptAsync(request, response, correlationId);
                    return;
                }

                await WriteAsync(response, 404, "application/json", "{\"error\":\"not found\"}");
            }
            catch (Exception ex)
            {
                try
                {
                    RollingFileLogger.Log("ERR", "Erro não tratado no request handler", ex);
                    await WriteAsync(response, 500, "application/json", "{\"error\":\"internal error\"}");
                }
                catch { }
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        private async Task HandleDecryptAsync(HttpListenerRequest request, HttpListenerResponse response, string correlationId)
        {
            // 1) Tamanho declarado: rejeita antes de ocupar slot ou ler o corpo.
            if (request.ContentLength64 > _options.MaxBodyBytes)
            {
                RollingFileLogger.Log("WRN", string.Format("Corpo {0}B excede o limite de {1}B", request.ContentLength64, _options.MaxBodyBytes));
                await WriteError(response, 413, "Corpo excede o tamanho máximo permitido.", correlationId);
                return;
            }

            // 2) Concorrência limitada.
            if (!await _gate.WaitAsync(_options.QueueWait))
            {
                RollingFileLogger.Log("WRN", "Servidor ocupado: limite de concorrência atingido");
                response.Headers["Retry-After"] = "1";
                await WriteError(response, 503, "Servidor ocupado. Tente novamente.", correlationId);
                return;
            }

            // O slot é liberado quando o trabalho realmente termina (mesmo após um timeout: a thread
            // não pode ser abortada, então continuar contando-a evita estourar o limite de concorrência).
            var work = Task.Run(() => DecryptWork(request, correlationId));
            var _ = work.ContinueWith(t => _gate.Release());

            using (var timeout = new CancellationTokenSource(_options.RequestTimeout))
            {
                var delay = Task.Delay(Timeout.Infinite, timeout.Token);
                if (await Task.WhenAny(work, delay) != work)
                {
                    RollingFileLogger.Log("ERR", "Timeout em /decrypt");
                    await WriteError(response, 504, "Tempo limite excedido.", correlationId);
                    return;
                }
            }

            var result = await work;
            await WriteAsync(response, result.StatusCode, result.ContentType, result.Body);
        }

        private DecryptResult DecryptWork(HttpListenerRequest request, string correlationId)
        {
            RollingFileLogger.Configure(_options.LogDir, correlationId);
            RollingFileLogger.Log("INF", "START /decrypt");

            // 3) Leitura limitada (cobre chunked, onde ContentLength64 = -1).
            string body;
            try { body = ReadBounded(request, _options.MaxBodyBytes); }
            catch (BodyTooLargeException)
            {
                RollingFileLogger.Log("WRN", "Corpo (chunked) excede o limite");
                return new DecryptResult(413, "application/json", SimpleJson.Serialize(
                    new DecryptErrorResponse("Corpo excede o tamanho máximo permitido.", correlationId)));
            }

            return RequestHandler.HandleDecrypt(correlationId, body,
                (level, message, ex) => RollingFileLogger.Log(level, message, ex));
        }

        private static string ReadBounded(HttpListenerRequest request, long max)
        {
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[16 * 1024];
                int n;
                while ((n = request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + n > max) throw new BodyTooLargeException();
                    ms.Write(buffer, 0, n);
                }
                return new UTF8Encoding(false).GetString(ms.ToArray());
            }
        }

        private sealed class BodyTooLargeException : Exception { }

        /// <summary>null = saudável. Resultado em cache curto para não poluir o log a cada poll de health.</summary>
        public string RunSelfTest()
        {
            lock (_selfTestLock)
            {
                if (DateTime.UtcNow - _selfTestAt < SelfTestCacheTtl) return _selfTestError;
                try
                {
                    var r = RequestHandler.HandleDecrypt("selftest", SelfTestPayload, (l, m, e) => { });
                    _selfTestError = r.StatusCode == 200 && r.Body == SelfTestExpected
                        ? null : "self-test retornou resultado inesperado";
                }
                catch (Exception ex) { _selfTestError = ex.Message; }
                _selfTestAt = DateTime.UtcNow;
                if (_selfTestError != null)
                    RollingFileLogger.Log("ERR", "Self-test de descriptografia falhou: " + _selfTestError);
                return _selfTestError;
            }
        }

        private static Task WriteError(HttpListenerResponse response, int status, string message, string correlationId)
        {
            return WriteAsync(response, status, "application/json",
                SimpleJson.Serialize(new DecryptErrorResponse(message, correlationId)));
        }

        private static async Task WriteAsync(HttpListenerResponse response, int statusCode, string contentType, string body)
        {
            response.StatusCode = statusCode;
            response.ContentType = contentType + "; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }
    }
}
