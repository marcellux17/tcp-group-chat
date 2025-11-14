using Helpers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Server
{
    internal class Server
    {
        const int HEARTBEAT_INTERVAL_IN_SEC = 5;
        const int HEARTBEAT_CHECK_LIMIT = 3;

        TcpListener _listener;

        ConcurrentDictionary<TcpClient, string> _clientToUsername;
        ConcurrentDictionary<string, TcpClient> _usernameToClient;

        ConcurrentDictionary<TcpClient, DateTime> _lastHeard;
        ConcurrentDictionary<TcpClient, SemaphoreSlim> _writeLocks;

        List<TcpClient> _viewers;
        SemaphoreSlim _viewersListLock;

        List<string> _messageHistory;
        SemaphoreSlim _messageHistoryLock;

        Channel<string> _messageQueue;
        public Server()
        {
            IPEndPoint ep = new IPEndPoint(IPAddress.Any, 56000);
            _listener = new TcpListener(ep);

            _clientToUsername = new ConcurrentDictionary<TcpClient, string>();
            _usernameToClient = new ConcurrentDictionary<string, TcpClient>();

            _lastHeard = new ConcurrentDictionary<TcpClient, DateTime>();
            _writeLocks = new ConcurrentDictionary<TcpClient, SemaphoreSlim>();

            _viewers = new List<TcpClient>();
            _viewersListLock = new SemaphoreSlim(1, 1);

            _messageQueue = Channel.CreateUnbounded<string>();

            _messageHistory = new List<string>();
            _messageHistoryLock = new SemaphoreSlim(1, 1);
        }
        public async Task Start()
        {
            _listener.Start();
            StartBroadcastLoop();
            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync();
                HandleNewClient(client);
            }
        }
        async Task HandleNewClient(TcpClient client)
        {
            _lastHeard[client] =  DateTime.UtcNow;
            _writeLocks[client] = new SemaphoreSlim(1, 1);
            Console.WriteLine($"Client connected: {client.Client.RemoteEndPoint.ToString()}");
            try
            {
                StartHeartBeatForClient(client);

                string? clientType = null;
                do
                {
                    int messageType = await NetworkHelper.GetMessageType(client);
                    _lastHeard[client] = DateTime.UtcNow;
                    int messageLength = await NetworkHelper.GetMessageLength(client);
                    string message = await NetworkHelper.GetMessage(client, messageLength);
                    if (messageType == 0)
                    {
                        clientType = message;
                    }
                } while (clientType == null);

                if (clientType == "messenger")
                {
                    await HandleMessenger(client);
                }
                else 
                {
                    await HandleViewer(client);
                }
                
            }
            catch (Exception ex)
            {
                await CloseClient(client);
            }
            
        }
        async Task HandleViewer(TcpClient client)
        {
            await _viewersListLock.WaitAsync();
            try
            {
                _viewers.Add(client);
            }
            finally
            {
                _viewersListLock.Release();
            }

            await SendMessageHistoryMessage(client);

            while (true)
            {
                await NetworkHelper.GetMessageType(client);
                _lastHeard[client] = DateTime.UtcNow;
                int messageLength =  await NetworkHelper.GetMessageLength(client);
                await NetworkHelper.GetMessage(client, messageLength);
            }
        }
        async Task SendMessageHistoryMessage(TcpClient client)
        {
            string messagesSoFar = "";
            await _messageHistoryLock.WaitAsync();
            try
            {
                foreach (string message in this._messageHistory)
                {
                    string messageWithDelimiter = "|" + message; 
                    messagesSoFar += messageWithDelimiter.Length + messageWithDelimiter;
                }
            }
            finally
            {
                _messageHistoryLock.Release();
            }
            await SendMessageToClient(client, 0, messagesSoFar);
        }
        async Task HandleMessenger(TcpClient client)
        {
            string? username = null;

            do
            {
                int messageType = await NetworkHelper.GetMessageType(client);
                _lastHeard[client] = DateTime.UtcNow;
                int messageLength = await NetworkHelper.GetMessageLength(client);
                string message = await NetworkHelper.GetMessage(client, messageLength);
                if (messageType == 0)
                {

                    username = message;
                    if (_usernameToClient.ContainsKey(username))
                    {
                        await SendMessageToClient(client, 0, "taken");
                    }
                }

            } while (username == null || _usernameToClient.ContainsKey(username));

            _usernameToClient[username] = client;
            _clientToUsername[client] = username;

            await SendMessageToClient(client, 0, "accepted");
            string userJoinedChatMessage = $"{username} has joined the chat";
            await EnqueueMessage(userJoinedChatMessage);

            await _messageHistoryLock.WaitAsync();
            try
            {
                _messageHistory.Add(userJoinedChatMessage);
            }
            finally
            {
                _messageHistoryLock.Release();
            }

            while (true)
            {
                int messageType = await NetworkHelper.GetMessageType(client);
                _lastHeard[client] = DateTime.UtcNow;
                int messageLength = await NetworkHelper.GetMessageLength(client);
                string message = await NetworkHelper.GetMessage(client, messageLength);

                if (messageType == 1) continue;

                string formattedMessage = $"[{username}]: {message}";
                
                await _messageHistoryLock.WaitAsync();
                try
                {
                    _messageHistory.Add(formattedMessage);
                }
                finally
                {
                    _messageHistoryLock.Release();
                }
                await EnqueueMessage(formattedMessage);
            }
        }
        async Task EnqueueMessage(string message)
        {
            await _messageQueue.Writer.WriteAsync(message);
        }
        async Task StartBroadcastLoop()
        {
            while (await _messageQueue.Reader.WaitToReadAsync())
            {
                while (_messageQueue.Reader.TryRead(out var message))
                {
                    List<TcpClient> snapShot;
                    await _viewersListLock.WaitAsync();
                    try
                    {
                        snapShot = _viewers.ToList();
                    }
                    finally{
                        _viewersListLock.Release();
                    }

                    var sendTasks = snapShot.Select(async client => {
                        try
                        {

                            await SendMessageToClient(client, 0, message);

                        }
                        catch (Exception ex)
                        {
                            await CloseClient(client);
                        }
                    });
                    await Task.WhenAll(sendTasks);
                }
            }
        }
        async Task StartHeartBeatForClient(TcpClient client)
        {
            int heartBeatChecksLeft = HEARTBEAT_CHECK_LIMIT;
            while (true)
            {
                DateTime latest;
                bool success = _lastHeard.TryGetValue(client, out latest);
                if (!success)
                {
                    break;
                }
                heartBeatChecksLeft--;
                if ((DateTime.UtcNow - latest).TotalSeconds <= HEARTBEAT_INTERVAL_IN_SEC * 2.5 && heartBeatChecksLeft >= 0)
                {
                    heartBeatChecksLeft = HEARTBEAT_CHECK_LIMIT;
                    await SendMessageToClient(client, 1, "PING");

                }
                else if (heartBeatChecksLeft >= 0)
                {
                    await SendMessageToClient(client, 1, "PING");

                }
                else
                {
                   
                    await CloseClient(client);
                    break;
                }
                await Task.Delay(HEARTBEAT_INTERVAL_IN_SEC * 1000);
                
            }
        }
        async Task CloseClient(TcpClient client)
        {
            if (_clientToUsername.TryRemove(client, out string? username))
            {
                _usernameToClient.TryRemove(username!, out _);
                string clientLeftMessage = $"{username} has left the chat.";
                await _messageHistoryLock.WaitAsync();
                try
                {
                    _messageHistory.Add(clientLeftMessage);
                }
                finally
                {
                    _messageHistoryLock.Release();
                }
                await EnqueueMessage(clientLeftMessage);

                _lastHeard.TryRemove(client, out _);
                _writeLocks.TryRemove(client, out _);
                
                Console.WriteLine($"Client disconnected: {client.Client.RemoteEndPoint?.ToString()}");
                
                client.Close();
            }
            else
            {

                await _viewersListLock.WaitAsync();
                try
                {
                    if (_viewers.Contains(client))
                    {
                        _viewers.Remove(client);
                        _lastHeard.TryRemove(client, out _);
                        _writeLocks.TryRemove(client, out _);
                        
                        Console.WriteLine($"Client disconnected: {client.Client.RemoteEndPoint?.ToString()}");

                        client.Close();
                    }
                }
                finally
                {
                    _viewersListLock.Release();
                }
            }
        }
        async Task SendMessageToClient(TcpClient client, int messageType, string message)
        {
            bool success = _writeLocks.TryGetValue(client, out var writeLock);
            if (success)
            {
                await writeLock.WaitAsync();
                try
                {
                    await NetworkHelper.SendMessage(messageType, client, message);
                }
                finally
                {
                    writeLock.Release();
                }
            }
        }
    }
}
