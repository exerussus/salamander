namespace Dsl.Tools.Lsp
{
    /// <summary>
    /// Точка входа LSP-сервера. Никаких аргументов: корень воркспейса приходит
    /// в initialize от клиента. Однопоточный цикл — для модкитного масштаба
    /// компиляция занимает миллисекунды, очередь сообщений не копится.
    /// </summary>
    public static class Program
    {
        public static int Main()
        {
            // stderr свободен от протокола — клиенты (LSP4IJ, VS Code) показывают
            // его в логах сервера; любая смерть должна оставлять внятный след.
            // Кодировку задаём явно: в перенаправленный stderr .NET по умолчанию
            // пишет в OEM-кодировке консоли (на русской Windows это cp866), а
            // редакторы читают UTF-8 — и лог сервера превращался в мусор ровно
            // там, где по нему ищут причину. stdout не трогаем: протокол пишет
            // байты сам (см. Rpc).
            System.Console.SetError(new System.IO.StreamWriter(
                System.Console.OpenStandardError(),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true });

            var lang = Server.EnvLanguage();
            System.Console.Error.WriteLine(Dsl.Text.Loc.T(lang, "salamander-lsp: запущен, жду initialize по stdio",
                "salamander-lsp: started, waiting for initialize over stdio"));
            try
            {
                var server = new Server(new Rpc());
                server.Run();
                lang = server.Language;
                System.Console.Error.WriteLine(Dsl.Text.Loc.T(lang, "salamander-lsp: клиент закрыл поток, выходим", "salamander-lsp: the client closed the stream, exiting"));
                return 0;
            }
            catch (System.Exception ex)
            {
                System.Console.Error.WriteLine(Dsl.Text.Loc.T(lang, "salamander-lsp: фатальная ошибка: " + ex, "salamander-lsp: fatal error: " + ex));
                return 1;
            }
        }
    }
}
