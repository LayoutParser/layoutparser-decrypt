using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LayoutParserDecrypt.Tests
{
    public class ServiceHardeningTests
    {
        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private static DecryptServer StartServer(Action<ServiceOptions> tweak, out int port)
        {
            port = FreePort();
            var o = new ServiceOptions
            {
                Port = port,
                LogDir = Path.Combine(Path.GetTempPath(), "lpdecrypt-tests-" + Guid.NewGuid())
            };
            tweak?.Invoke(o);
            var s = new DecryptServer(o);
            s.Start();
            return s;
        }

        [Fact]
        public void Options_Defaults_SaoLoopbackELimitesSeguros()
        {
            var o = ServiceOptions.FromEnvironment(_ => null);
            Assert.True(o.IsLoopbackOnly);
            Assert.Equal(5220, o.Port);
            Assert.Equal(4, o.MaxConcurrency);
        }

        [Fact]
        public void Options_LeVariaveisEIgnoraInvalidas()
        {
            var env = new System.Collections.Generic.Dictionary<string, string>
            {
                ["LAYOUTPARSER_DECRYPT_BIND"] = "10.0.0.5, localhost",
                ["LAYOUTPARSER_DECRYPT_PORT"] = "6000",
                ["LAYOUTPARSER_DECRYPT_MAX_CONCURRENCY"] = "abc",
                ["LAYOUTPARSER_DECRYPT_TIMEOUT_SECONDS"] = "5",
            };
            var o = ServiceOptions.FromEnvironment(k => env.TryGetValue(k, out var v) ? v : null);
            Assert.False(o.IsLoopbackOnly);
            Assert.Equal(new[] { "http://10.0.0.5:6000/", "http://localhost:6000/" }, o.Prefixes());
            Assert.Equal(4, o.MaxConcurrency);
            Assert.Equal(TimeSpan.FromSeconds(5), o.RequestTimeout);
        }

        [Fact]
        public async Task Health_ExecutaSelfTest_Devolve200()
        {
            var server = StartServer(null, out var port);
            try
            {
                using (var c = new HttpClient())
                {
                    var r = await c.GetAsync("http://localhost:" + port + "/health");
                    var body = await r.Content.ReadAsStringAsync();
                    Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                    Assert.Equal("{\"status\":\"ok\"}", body);
                }
            }
            finally { server.Stop(); }
        }

        [Fact]
        public void SelfTest_PayloadEmbutido_Decripta()
        {
            var server = StartServer(null, out _);
            try { Assert.Null(server.RunSelfTest()); }
            finally { server.Stop(); }
        }

        [Fact]
        public async Task Decrypt_CorpoMaiorQueLimite_Devolve413()
        {
            var server = StartServer(o => o.MaxBodyBytes = 16, out var port);
            try
            {
                using (var c = new HttpClient())
                {
                    var r = await c.PostAsync("http://localhost:" + port + "/decrypt",
                        new StringContent(new string('A', 100), Encoding.UTF8, "text/plain"));
                    Assert.Equal((HttpStatusCode)413, r.StatusCode);
                }
            }
            finally { server.Stop(); }
        }

        [Fact]
        public async Task Decrypt_Chunked_MaiorQueLimite_Devolve413()
        {
            var server = StartServer(o => o.MaxBodyBytes = 16, out var port);
            try
            {
                using (var c = new HttpClient())
                {
                    var content = new StringContent(new string('A', 100), Encoding.UTF8, "text/plain");
                    var req = new HttpRequestMessage(HttpMethod.Post, "http://localhost:" + port + "/decrypt") { Content = content };
                    req.Headers.TransferEncodingChunked = true;
                    var r = await c.SendAsync(req);
                    Assert.Equal((HttpStatusCode)413, r.StatusCode);
                }
            }
            finally { server.Stop(); }
        }

        [Fact]
        public async Task Decrypt_RotaDesconhecida_Devolve404()
        {
            var server = StartServer(null, out var port);
            try
            {
                using (var c = new HttpClient())
                {
                    var r = await c.GetAsync("http://localhost:" + port + "/nope");
                    Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
                }
            }
            finally { server.Stop(); }
        }

        [Fact]
        public async Task Shutdown_Gracioso_AguardaRequestEmAndamento()
        {
            var server = StartServer(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10), out var port);
            const string payload = "XXXiaaOXUaXEcxyAReiZ5fZJA==";
            using (var tcp = new System.Net.Sockets.TcpClient("localhost", port))
            {
                var stream = tcp.GetStream();
                var head = "POST /decrypt HTTP/1.1\r\nHost: localhost\r\nContent-Type: text/plain\r\nContent-Length: "
                    + payload.Length + "\r\nConnection: close\r\n\r\n";
                var headBytes = Encoding.ASCII.GetBytes(head + payload.Substring(0, 5));
                stream.Write(headBytes, 0, headBytes.Length);
                await Task.Delay(300); // request está "em andamento" (corpo incompleto)

                var stopTask = Task.Run(() => server.Stop());
                await Task.Delay(300);
                Assert.False(stopTask.IsCompleted, "Stop deveria aguardar o request em andamento");

                var rest = Encoding.ASCII.GetBytes(payload.Substring(5));
                stream.Write(rest, 0, rest.Length);

                var text = await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync();
                await stopTask;
                Assert.Contains("200 OK", text);
                Assert.Contains("LPD-SELFTEST-OK", text);
            }
        }

        private static DecryptServer StartWithDecrypt(Action<ServiceOptions> tweak, Func<string, string, DecryptResult> decrypt, out int port)
        {
            port = FreePort();
            var o = new ServiceOptions
            {
                Port = port,
                LogDir = Path.Combine(Path.GetTempPath(), "lpdecrypt-tests-" + Guid.NewGuid())
            };
            tweak?.Invoke(o);
            var s = new DecryptServer(o, decrypt);
            s.Start();
            return s;
        }

        [Fact]
        public async Task Decrypt_AcimaDoLimiteDeConcorrencia_Devolve503ComRetryAfter()
        {
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var server = StartWithDecrypt(
                o => { o.MaxConcurrency = 1; o.QueueWait = TimeSpan.FromMilliseconds(100); },
                (corr, body) => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); return new DecryptResult(200, "text/plain", "ok"); },
                out var port);
            try
            {
                using (var c = new HttpClient())
                {
                    var url = "http://localhost:" + port + "/decrypt";
                    var first = c.PostAsync(url, new StringContent("XXXabc", Encoding.UTF8, "text/plain"));
                    Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "primeira requisição não chegou ao decrypt");

                    var second = await c.PostAsync(url, new StringContent("XXXabc", Encoding.UTF8, "text/plain"));
                    Assert.Equal((HttpStatusCode)503, second.StatusCode);
                    Assert.True(second.Headers.Contains("Retry-After"));

                    release.Set();
                    Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
                }
            }
            finally { release.Set(); server.Stop(); }
        }

        [Fact]
        public async Task Decrypt_QueDemoraMaisQueOTimeout_Devolve504()
        {
            var release = new ManualResetEventSlim(false);
            var server = StartWithDecrypt(
                o => o.RequestTimeout = TimeSpan.FromMilliseconds(300),
                (corr, body) => { release.Wait(TimeSpan.FromSeconds(10)); return new DecryptResult(200, "text/plain", "ok"); },
                out var port);
            try
            {
                using (var c = new HttpClient())
                {
                    var r = await c.PostAsync("http://localhost:" + port + "/decrypt",
                        new StringContent("XXXabc", Encoding.UTF8, "text/plain"));
                    var body = await r.Content.ReadAsStringAsync();
                    Assert.Equal((HttpStatusCode)504, r.StatusCode);
                    Assert.Contains("\"error\"", body);
                }
            }
            finally { release.Set(); server.Stop(); }
        }
    }
}
