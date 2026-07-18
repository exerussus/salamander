using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dsl.Tools.Lsp
{
    /// <summary>
    /// JSON-RPC поверх stdio с фреймингом LSP (Content-Length). Рукописный и
    /// минимальный: ровно те возможности протокола, что нужны серверу.
    /// </summary>
    public sealed class Rpc
    {
        private readonly Stream _in = Console.OpenStandardInput();
        private readonly Stream _out = Console.OpenStandardOutput();
        private readonly object _writeLock = new object();

        /// <summary>Следующее сообщение клиента; null — поток закрыт (пора выходить).</summary>
        public JObject Read()
        {
            int length = -1;
            while (true)
            {
                string line = ReadHeaderLine();
                if (line == null) return null;
                if (line.Length == 0) break; // конец заголовков
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.Substring("Content-Length:".Length).Trim());
            }
            if (length < 0) return null;

            var buf = new byte[length];
            int off = 0;
            while (off < length)
            {
                int n = _in.Read(buf, off, length - off);
                if (n <= 0) return null;
                off += n;
            }
            return JObject.Parse(Encoding.UTF8.GetString(buf));
        }

        private string ReadHeaderLine()
        {
            var sb = new StringBuilder(64);
            while (true)
            {
                int b = _in.ReadByte();
                if (b < 0) return null;          // EOF
                if (b == '\n') break;
                if (b != '\r') sb.Append((char)b);
            }
            return sb.ToString();
        }

        private void Send(JObject msg)
        {
            var body = Encoding.UTF8.GetBytes(msg.ToString(Formatting.None));
            var head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            lock (_writeLock)
            {
                _out.Write(head, 0, head.Length);
                _out.Write(body, 0, body.Length);
                _out.Flush();
            }
        }

        public void Reply(JToken id, JToken result) =>
            Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? JValue.CreateNull() });

        public void Error(JToken id, int code, string message) =>
            Send(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            });

        public void Notify(string method, JToken @params) =>
            Send(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params });
    }
}
