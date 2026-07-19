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
            // его в логах сервера; любая смерть должна оставлять внятный след
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
