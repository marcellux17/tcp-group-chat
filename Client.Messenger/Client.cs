using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Helpers;

namespace Client.Messenger
{
    internal class Client
    {
        const int HEARTBEAT_INTERVAL_IN_SEC = 5;

        DateTime _lastMessage;

        readonly TcpClient _socket;
        readonly IPEndPoint _remoteEndPoint;

        readonly SemaphoreSlim _writeLock;
        readonly Channel<string> _normalMessages;

        int _connectionAliveFlag;

        public Client(IPAddress serverAddress, Int32 serverPort)
        {
            _remoteEndPoint = new IPEndPoint(serverAddress, serverPort);
            _socket = new TcpClient(new IPEndPoint(IPAddress.Any, 0));
            _connectionAliveFlag = 0;
            _writeLock = new SemaphoreSlim(1, 1);
            _normalMessages = Channel.CreateUnbounded<string>();
        }
        public async Task Connect()
        {
            try
            {
                _socket.Connect(_remoteEndPoint);
                _connectionAliveFlag = 1;
                Console.WriteLine("Welcome to group chat!");
                Console.WriteLine("Open a message viewer to see messages!");

                await SendMessage(0, "messenger");

                Listen();
                string response = "taken";
                do
                {
                    Console.Write("Choose a username: ");
                    string username = Console.ReadLine();

                    await SendMessage(0, username);
                    bool canRead = await _normalMessages.Reader.WaitToReadAsync();
                    if (canRead)
                    {
                        response = await _normalMessages.Reader.ReadAsync();
                    }
                    Console.Clear();
                } while (response != "accepted" && Interlocked.CompareExchange(ref _connectionAliveFlag, 1, 1) == 1);

                CheckHeartBeats();
                

                while (Interlocked.CompareExchange(ref _connectionAliveFlag, 1, 1) == 1)
                {
                    Console.Clear();
                    Console.Write("Enter message: ");
                    string messageToSend = Console.ReadLine();
                    await SendMessage(0, messageToSend);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                await CloseClient();
            }

        }
        async Task Listen()
        {
            while (Interlocked.CompareExchange(ref _connectionAliveFlag, 1, 1) == 1)
            {
                int messageType = await NetworkHelper.GetMessageType(_socket);
                _lastMessage = DateTime.UtcNow;
                int messageLength = await NetworkHelper.GetMessageLength(_socket);
                string message = await NetworkHelper.GetMessage(_socket, messageLength);
                if (messageType == 1)
                {

                    await SendMessage(1, "PONG");
                }
                else
                {
                    await _normalMessages.Writer.WriteAsync(message);
                }
            }
        }
       async Task CheckHeartBeats()
        {
            while (Interlocked.CompareExchange(ref _connectionAliveFlag, 1, 1) == 1)
            {
                if ((DateTime.UtcNow - _lastMessage).TotalSeconds > HEARTBEAT_INTERVAL_IN_SEC * 2.5)
                {
                    await CloseClient();
                    return;
                }

                await Task.Delay(HEARTBEAT_INTERVAL_IN_SEC * 1000);
            }
        }
        async Task CloseClient()
        {
            if (Interlocked.Exchange(ref _connectionAliveFlag, 0) == 1)
            {
                Console.WriteLine($"Server unavailble, client shutting down");
                _socket.Close();
                await Task.Delay(500);
                Environment.Exit(0);
            }

        }
        async Task SendMessage(int messageType, string message)
        {
            await _writeLock.WaitAsync();
            try
            {
                await NetworkHelper.SendMessage(messageType, _socket, message);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
