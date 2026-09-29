using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LayoutParserDecrypt;
using Xunit;

namespace LayoutParserDecrypt.Tests
{
    /// <summary>
    /// Testes do endpoint HTTP <c>POST /decrypt</c> (ADR segregação Decrypt/LowCodeRunner, 2026-09-25).
    ///
    /// Não há gabarito legado (par cifra/claro conhecido) versionado neste repo — não é reintroduzido
    /// aqui por decisão de escopo. Os testes de "caminho feliz" fazem round-trip: cifram um texto
    /// conhecido com os MESMOS parâmetros (chave/IV/algoritmo RijndaelManaged) usados por
    /// <c>LayoutParserLib.CryptographySysMiddle</c> e verificam que o handler devolve o texto original.
    ///
    /// Roda em net48 (não net10.0) — ver comentário no .csproj do projeto principal: a chave (96 bits)
    /// e o IV (152 bits) usados aqui não são tamanhos válidos de AES, e a partir do .NET Core/.NET 5+
    /// RijndaelManaged é só um shim sobre AES. Testado empiricamente nesta etapa: o mesmo código, em
    /// net10.0, lança "Specified key is not a valid size for this algorithm" para essa chave/IV real.
    /// </summary>
    public class DecryptEndpointTests
    {
        private static string EncryptLikeCryptographySysMiddle(string plainText)
        {
            using (var algorithm = new RijndaelManaged())
            using (var encryptor = algorithm.CreateEncryptor(
                Encoding.UTF8.GetBytes("dbc%$#h92785"),
                Encoding.UTF8.GetBytes("Ca#&UjO){Qwz*@FcsPs")))
            using (var memoryStream = new MemoryStream())
            {
                using (var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write))
                {
                    var inputBytes = Encoding.UTF8.GetBytes(plainText);
                    cryptoStream.Write(inputBytes, 0, inputBytes.Length);
                }
                return Convert.ToBase64String(memoryStream.ToArray());
            }
        }

        [Fact]
        public void HandleDecrypt_ComPayloadValido_DevolveTextoOriginal()
        {
            const string plainText = "<xml>conteudo de teste do LayoutParserDecrypt</xml>";
            var cipherBase64 = EncryptLikeCryptographySysMiddle(plainText);

            // O serviço descarta os 3 primeiros caracteres antes de decriptar (comportamento herdado
            // do Program.cs legado — preservado). Prefixamos com 3 chars quaisquer para simular isso.
            var payload = "XXX" + cipherBase64;

            var result = RequestHandler.HandleDecrypt("test-corr-1", payload, (level, msg, ex) => { });

            Assert.Equal(200, result.StatusCode);
            Assert.Equal("text/plain", result.ContentType);
            Assert.Equal(plainText, result.Body);
        }

        [Fact]
        public void HandleDecrypt_ComCorpoVazio_Devolve400ComContratoEstruturado()
        {
            var result = RequestHandler.HandleDecrypt("test-corr-2", string.Empty, (level, msg, ex) => { });

            Assert.Equal(400, result.StatusCode);
            Assert.Equal("application/json", result.ContentType);
            Assert.Contains("\"correlationId\":\"test-corr-2\"", result.Body);
        }

        [Fact]
        public void HandleDecrypt_ComPayloadInvalido_Devolve422ComContratoEstruturado()
        {
            // não é Base64 válido após o strip dos 3 primeiros chars -> CryptographySysMiddle.Decrypt lança
            var result = RequestHandler.HandleDecrypt("test-corr-3", "XXXnao-e-base64-valido!!!", (level, msg, ex) => { });

            Assert.Equal(422, result.StatusCode);
            Assert.Equal("application/json", result.ContentType);
            Assert.Contains("\"correlationId\":\"test-corr-3\"", result.Body);
        }

        [Fact]
        public async Task Servico_RealHttp_DecryptEHealth_RespondemCorretamente()
        {
            // Integração fim-a-fim: sobe o HttpListener de verdade numa porta efêmera e faz requests
            // HTTP reais — prova que a ligação Program -> RequestHandler -> resposta está correta,
            // não só a lógica isolada dos testes acima.
            var port = GetFreeTcpPort();
            using (var cts = new CancellationTokenSource())
            {
                var logDir = Path.Combine(Path.GetTempPath(), "lpdecrypt-tests-" + Guid.NewGuid());
                var serverTask = Program.RunServerAsync(port, logDir, cts.Token);

                try
                {
                    using (var client = new HttpClient { BaseAddress = new Uri("http://localhost:" + port + "/") })
                    {
                        // Pequena espera para o listener abrir a porta antes do primeiro request.
                        HttpResponseMessage healthResponse = null;
                        for (var attempt = 0; attempt < 20; attempt++)
                        {
                            try
                            {
                                healthResponse = await client.GetAsync("health");
                                break;
                            }
                            catch (HttpRequestException)
                            {
                                await Task.Delay(50);
                            }
                        }

                        Assert.NotNull(healthResponse);
                        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

                        const string plainText = "conteudo real de integracao";
                        var cipherBase64 = EncryptLikeCryptographySysMiddle(plainText);
                        var content = new StringContent("XXX" + cipherBase64, Encoding.UTF8, "text/plain");
                        content.Headers.Add("X-Correlation-ID", "integration-corr");

                        var decryptResponse = await client.PostAsync("decrypt", content);
                        var body = await decryptResponse.Content.ReadAsStringAsync();

                        Assert.Equal(HttpStatusCode.OK, decryptResponse.StatusCode);
                        Assert.Equal(plainText, body);
                    }
                }
                finally
                {
                    cts.Cancel();
                    try { await serverTask; } catch { }
                }
            }
        }

        private static int GetFreeTcpPort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
