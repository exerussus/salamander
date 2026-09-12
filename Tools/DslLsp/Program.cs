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

            System.Console.Error.WriteLine("salamander-lsp: запущен, жду initialize по stdio");
            try
            {
                var server = new Server(new Rpc());
                server.Run();
                System.Console.Error.WriteLine("salamander-lsp: клиент закрыл поток, выходим");
                return 0;
            }
            catch (System.Exception ex)
            {
                System.Console.Error.WriteLine("salamander-lsp: фатальная ошибка: " + ex);
                return 1;
            }
        }
    }
}
