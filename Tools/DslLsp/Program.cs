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
            var server = new Server(new Rpc());
            server.Run();
            return 0;
        }
    }
}
