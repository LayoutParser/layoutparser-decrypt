using System;
using LayoutParserLib;

namespace LayoutParserDecrypt
{
    /// <summary>Contrato de erro estruturado — substitui o antigo exit code + stderr do CLI.</summary>
    public class DecryptErrorResponse
    {
        public string Error { get; set; }
        public string CorrelationId { get; set; }

        public DecryptErrorResponse(string error, string correlationId)
        {
            Error = error;
            CorrelationId = correlationId;
        }
    }

    public class DecryptResult
    {
        public int StatusCode { get; }
        public string ContentType { get; }
        public string Body { get; }

        public DecryptResult(int statusCode, string contentType, string body)
        {
            StatusCode = statusCode;
            ContentType = contentType;
            Body = body;
        }
    }

    /// <summary>
    /// Lógica do endpoint <c>POST /decrypt</c>, extraída do laço de <see cref="HttpListener"/>
    /// (<see cref="Program"/>) para ser testável sem precisar abrir uma porta TCP real em cada teste.
    ///
    /// Contrato (ADR segregação Decrypt/LowCodeRunner, 2026-09-25):
    ///   Body de entrada: texto (UTF-8) com o conteúdo cifrado — mesmo texto que antes ia para o
    ///     arquivo temporário de entrada do CLI legado.
    ///   200 text/plain: conteúdo decriptado.
    ///   400 application/json <see cref="DecryptErrorResponse"/>: corpo vazio.
    ///   422 application/json <see cref="DecryptErrorResponse"/>: falha na descriptografia.
    /// </summary>
    public static class RequestHandler
    {
        public static DecryptResult HandleDecrypt(string correlationId, string encryptedContent, Action<string, string, Exception> log)
        {
            if (string.IsNullOrEmpty(encryptedContent))
            {
                log("ERR", "Corpo da requisição vazio", null);
                return new DecryptResult(400, "application/json", SimpleJson.Serialize(
                    new DecryptErrorResponse("Corpo da requisição vazio. Nada a descriptografar.", correlationId)));
            }

            try
            {
                // Mesmo comportamento do CLI legado: os 3 primeiros caracteres do payload são
                // descartados antes de decriptar (prefixo/marcador do produtor do conteúdo).
                var contentToDecrypt = encryptedContent.Length > 3 ? encryptedContent.Substring(3) : encryptedContent;

                log("INF", string.Format("Decrypting {0} chars", contentToDecrypt.Length), null);
                var decrypted = CryptographySysMiddle.Decrypt(contentToDecrypt);
                log("INF", string.Format("END success outputChars={0}", decrypted != null ? decrypted.Length : 0), null);

                return new DecryptResult(200, "text/plain", decrypted ?? string.Empty);
            }
            catch (Exception ex)
            {
                log("ERR", "FATAL decrypt", ex);
                return new DecryptResult(422, "application/json", SimpleJson.Serialize(
                    new DecryptErrorResponse("Falha ao descriptografar conteúdo: " + ex.Message, correlationId)));
            }
        }
    }
}
