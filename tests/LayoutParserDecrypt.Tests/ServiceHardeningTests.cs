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
                    Assert.Contains("\"status\":\"ok\"", body);
                    Assert.Contains("\"selfTest\":\"ok\"", body);
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
    }
}
