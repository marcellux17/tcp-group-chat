namespace Server
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Server server = new Server();
            await server.Start();
        }
    }
}
