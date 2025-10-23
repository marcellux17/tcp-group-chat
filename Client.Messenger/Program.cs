using System.Net;

namespace Client.Messenger
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Int32 serverPort = 56000;
            Client client = new Client(IPAddress.Parse("192.168.1.173"), serverPort);
            await client.Connect();

        }
    }
}
