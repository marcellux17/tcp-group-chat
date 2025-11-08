using Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Server
{
    internal class Server
    {
        const int heartBeatIntervalInSec = 5;
        const int heartBeatChecksLimit = 3;

        TcpListener listener;

        ConcurrentDictionary<TcpClient, string> clientToUsername;
        ConcurrentDictionary<string, TcpClient> usernameToClient;

        ConcurrentDictionary<TcpClient, DateTime> lastHeard;
        ConcurrentDictionary<TcpClient, SemaphoreSlim> writeLocks;

        List<TcpClient> viewers;
        SemaphoreSlim viewersListLock;

        List<string> messageHistory;
        SemaphoreSlim messageHistoryLock;

        Channel<string> messageQueue;
        public Server()
        {
            IPEndPoint ep = new IPEndPoint(IPAddress.Any, 56000);
            listener = new TcpListener(ep);

            clientToUsername = new ConcurrentDictionary<TcpClient, string>();
            usernameToClient = new ConcurrentDictionary<string, TcpClient>();

            lastHeard = new ConcurrentDictionary<TcpClient, DateTime>();
            writeLocks = new ConcurrentDictionary<TcpClient, SemaphoreSlim>();

            viewers = new List<TcpClient>();
            viewersListLock = new SemaphoreSlim(1, 1);

            messageQueue = Channel.CreateUnbounded<string>();

            messageHistory = new List<string>();
            messageHistoryLock = new SemaphoreSlim(1, 1);
        }
        public async Task Start()
        {
            listener.Start();
            StartBroadcastLoop();
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                HandleNewClient(client);
            }
        }
        private async Task HandleNewClient(TcpClient client)
        {
            lastHeard[client] =  DateTime.UtcNow;
            writeLocks[client] = new SemaphoreSlim(1, 1);
            Console.WriteLine($"Client connected: {client.Client.RemoteEndPoint.ToString()}");
            try
            {
                StartHeartBeatForClient(client);

                string? clientType = null;
                do
                {
                    int messageType = await NetworkHelper.GetMessageType(client);
                    lastHeard[client] = DateTime.UtcNow;
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
        private async Task HandleViewer(TcpClient client)
        {
            await viewersListLock.WaitAsync();
            try
            {
                viewers.Add(client);
            }
            finally
            {
                viewersListLock.Release();
            }

            await SendMessageHistoryMessage(client);

            while (true)
            {
                await NetworkHelper.GetMessageType(client);
                lastHeard[client] = DateTime.UtcNow;
                int messageLength =  await NetworkHelper.GetMessageLength(client);
                await NetworkHelper.GetMessage(client, messageLength);
            }
        }
        private async Task SendMessageHistoryMessage(TcpClient client)
        {
            string messagesSoFar = "";
            await messageHistoryLock.WaitAsync();
            try
            {
                foreach (string message in this.messageHistory)
                {
                    string messageWithDelimiter = "|" + message; 
                    messagesSoFar += messageWithDelimiter.Length + messageWithDelimiter;
                }
            }
            finally
            {
                messageHistoryLock.Release();
            }
            await SendMessageToClient(client, 0, messagesSoFar);
        }
        private async Task HandleMessenger(TcpClient client)
        {
            string? username = null;

            do
            {
                int messageType = await NetworkHelper.GetMessageType(client);
                lastHeard[client] = DateTime.UtcNow;
                int messageLength = await NetworkHelper.GetMessageLength(client);
                string message = await NetworkHelper.GetMessage(client, messageLength);
                if (messageType == 0)
                {

                    username = message;
                    if (usernameToClient.ContainsKey(username))
                    {
                        await SendMessageToClient(client, 0, "taken");
                    }
                }

            } while (username == null || usernameToClient.ContainsKey(username));

            usernameToClient[username] = client;
            clientToUsername[client] = username;

            await SendMessageToClient(client, 0, "accepted");
            string userJoinedChatMessage = $"{username} has joined the chat";
            await EnqueueMessage(userJoinedChatMessage);

            await messageHistoryLock.WaitAsync();
            try
            {
                messageHistory.Add(userJoinedChatMessage);
            }
            finally
            {
                messageHistoryLock.Release();
            }

            while (true)
            {
                int messageType = await NetworkHelper.GetMessageType(client);
                lastHeard[client] = DateTime.UtcNow;
                int messageLength = await NetworkHelper.GetMessageLength(client);
                string message = await NetworkHelper.GetMessage(client, messageLength);

                if (messageType == 1) continue;

                string formattedMessage = $"[{username}]: {message}";
                
                await messageHistoryLock.WaitAsync();
                try
                {
                    messageHistory.Add(formattedMessage);
                }
                finally
                {
                    messageHistoryLock.Release();
                }
                await EnqueueMessage(formattedMessage);
            }
        }
        private async Task EnqueueMessage(string message)
        {
            await messageQueue.Writer.WriteAsync(message);
        }
        private async Task StartBroadcastLoop()
        {
            while (await messageQueue.Reader.WaitToReadAsync())
            {
                while (messageQueue.Reader.TryRead(out var message))
                {
                    List<TcpClient> snapShot;
                    await viewersListLock.WaitAsync();
                    try
                    {
                        snapShot = viewers.ToList();
                    }
                    finally{
                        viewersListLock.Release();
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
        private async Task StartHeartBeatForClient(TcpClient client)
        {
            int heartBeatChecksLeft = heartBeatChecksLimit;
            while (true)
            {
                DateTime latest;
                bool success = lastHeard.TryGetValue(client, out latest);
                if (!success)
                {
                    break;
                }
                heartBeatChecksLeft--;
                if ((DateTime.UtcNow - latest).TotalSeconds <= heartBeatIntervalInSec * 2.5 && heartBeatChecksLeft >= 0)
                {
                    heartBeatChecksLeft = heartBeatChecksLimit;
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
                await Task.Delay(heartBeatIntervalInSec * 1000);
                
            }
        }
        private async Task CloseClient(TcpClient client)
        {
            if (clientToUsername.TryRemove(client, out string? username))
            {
                usernameToClient.TryRemove(username!, out _);
                string clientLeftMessage = $"{username} has left the chat.";
                await messageHistoryLock.WaitAsync();
                try
                {
                    messageHistory.Add(clientLeftMessage);
                }
                finally
                {
                    messageHistoryLock.Release();
                }
                await EnqueueMessage(clientLeftMessage);

                lastHeard.TryRemove(client, out _);
                writeLocks.TryRemove(client, out _);
                
                Console.WriteLine($"Client disconnected: {client.Client.RemoteEndPoint?.ToString()}");
                
                client.Close();
            }
            else
            {

                await viewersListLock.WaitAsync();
                try
                {
                    if (viewers.Contains(client))
                    {
                        viewers.Remove(client);
                        lastHeard.TryRemove(client, out _);
                        writeLocks.TryRemove(client, out _);
                        
                        Console.WriteLine($"Client disconnected: {client.Client.RemoteEndPoint?.ToString()}");

                        client.Close();
                    }
                }
                finally
                {
                    viewersListLock.Release();
                }
            }
        }
        private async Task SendMessageToClient(TcpClient client, int messageType, string message)
        {
            bool success = writeLocks.TryGetValue(client, out var writeLock);
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
