using System.Text;

namespace LayoutParserDecrypt
{
    /// <summary>
    /// Serializador JSON mínimo e propositalmente burro — só escapa o suficiente para o contrato de
    /// erro estruturado (2 campos string). Evita puxar System.Text.Json/Newtonsoft só para isso num
    /// projeto net48 sem dependências NuGet.
    /// </summary>
    internal static class SimpleJson
    {
        public static string Serialize(DecryptErrorResponse response)
        {
            var sb = new StringBuilder();
            sb.Append("{\"error\":\"").Append(Escape(response.Error))
              .Append("\",\"correlationId\":\"").Append(Escape(response.CorrelationId))
              .Append("\"}");
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
