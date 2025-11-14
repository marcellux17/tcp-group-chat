
using System.Net;
using System.Net.Sockets;
using Helpers;

namespace Client.Viewer
{
    internal class Client
    {
        const int HEARTBEAT_INTERVAL_IN_SEC = 5;
        const int HEARTBEAT_CHECKS_LIMIT = 3;

        DateTime _lastMessage;
        TcpClient _socket;
        IPEndPoint _remoteEndPoint;

        int _connectionAliveFlag;
        public Client(IPAddress serverAddress, Int32 serverPort)
        {
            _remoteEndPoint = new IPEndPoint(serverAddress, serverPort);
            _socket = new TcpClient(new IPEndPoint(IPAddress.Any, 0));
            _connectionAliveFlag = 0;
        }
        public async Task Connect()
        {
            try
            {
                _socket.Connect(_remoteEndPoint);
                _connectionAliveFlag = 1;
                CheckHeartBeats();
                await SendMessage(0, "viewer");

                string? messageHistory = null;
                do
                {
                    int messageType = await NetworkHelper.GetMessageType(_socket);
                    _lastMessage = DateTime.UtcNow;
                    int messageLength = await NetworkHelper.GetMessageLength(_socket);
                    string message = await NetworkHelper.GetMessage(_socket, messageLength);
                    if (messageType == 0)
                    {
                        messageHistory = message;
                    }
                    else
                    {
                        await SendMessage(1, "PONG");
                    }
                } while (messageHistory == null);

                PrintMessageHistory(messageHistory);

                await Listen();
            }
            catch (Exception ex)
            {
                await CloseClient();
            }

        }
        void PrintMessageHistory(string messageHistory)
        {
            if (messageHistory != String.Empty)
            {
                int currentCharIndex = 0;
                do
                {
                    string digits = "";
                    while (currentCharIndex < messageHistory.Length && char.IsDigit(messageHistory[currentCharIndex]))
                    {
                        digits += messageHistory[currentCharIndex];
                        currentCharIndex++;
                    }
                    int messageToBeProcessedLength = int.Parse(digits);

                    string currentMessageBeingProcessed = "";

                    int currentMessageBeingProcessedEndCharIndex = currentCharIndex + messageToBeProcessedLength - 1;

                    currentCharIndex++;

                    while (currentCharIndex <= currentMessageBeingProcessedEndCharIndex && currentCharIndex < messageHistory.Length)
                    {
                        currentMessageBeingProcessed += messageHistory[currentCharIndex];
                        currentCharIndex++;
                    }
                    Console.WriteLine(currentMessageBeingProcessed);
                    Console.WriteLine("----------------------------------------------");
                } while (currentCharIndex < messageHistory.Length);

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
                    Console.WriteLine(message);
                    Console.WriteLine("----------------------------------------------");
                }
            }
        }
        async Task CheckHeartBeats()
        {
            int heartBeatChecksLeft = HEARTBEAT_CHECKS_LIMIT;
            while (Interlocked.CompareExchange(ref _connectionAliveFlag, 1, 1) == 1)
            {
                heartBeatChecksLeft--;
                if ((DateTime.UtcNow - _lastMessage).TotalSeconds <= HEARTBEAT_INTERVAL_IN_SEC * 2.5 && heartBeatChecksLeft >= 0)
                {
                    heartBeatChecksLeft = HEARTBEAT_CHECKS_LIMIT;
                }
                else if (heartBeatChecksLeft < 0)
                {
                    await CloseClient();
                }
                if (Interlocked.CompareExchange(ref _connectionAliveFlag, 1, 1) == 1)
                {
                    await Task.Delay(HEARTBEAT_INTERVAL_IN_SEC * 1000);
                }
            }
        }
        async Task CloseClient()
        {
            if (Interlocked.Exchange(ref _connectionAliveFlag, 0) == 1)
            {
                Console.WriteLine($"Server unavailble, client shutting down");
                _socket.Close();
                await Task.Delay(750);
                Environment.Exit(0);
            }

        }
        async Task SendMessage(int messageType, string message)
        { 
           await NetworkHelper.SendMessage(messageType, _socket, message);
            
        }
    }
}
