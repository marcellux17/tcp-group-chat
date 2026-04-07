using System.Net;

namespace Client.Viewer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Int32 serverPort;
            while (true)
            {
                Console.Write("Server is on port: ");
                string portString = Console.ReadLine();
                if (int.TryParse(portString, out serverPort))
                {
                    break;
                }
                Console.WriteLine("Port could not be parsed.");
            }

            Client client = new Client(IPAddress.Parse("192.168.1.173"), serverPort);
            await client.Connect();

        }
    }
}
