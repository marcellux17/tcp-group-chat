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

        //for messengers
        ConcurrentDictionary<TcpClient, string> clientToUsername;//when broadcasting we associate incoming messages from a socket with usernames
        ConcurrentDictionary<string, TcpClient> usernameToClient;//for quickly checking if a username is taken
        
        //for all clients
        ConcurrentDictionary<TcpClient, DateTime> lastHeard;//DateTime will hold timestamps of when the server last heard from the client its for checking connection lost
        ConcurrentDictionary<TcpClient, SemaphoreSlim> writeLocks;//need locks on the write stream because two loops could access it concurrently: heartbeat loop and response loop

        //for viewers
        List<TcpClient> viewers;
        SemaphoreSlim viewersListLock;

        //message history for viewers that connect later
        List<string> messageHistory;
        SemaphoreSlim messageHistoryLock;

        //message queue for messages
        Channel<string> messageQueue;
        public Server()
        {
            IPEndPoint ep = new IPEndPoint(IPAddress.Any, 56000);
            listener = new TcpListener(ep);

            //messengers
            clientToUsername = new ConcurrentDictionary<TcpClient, string>();
            usernameToClient = new ConcurrentDictionary<string, TcpClient>();

            //for all clients
            lastHeard = new ConcurrentDictionary<TcpClient, DateTime>();
            writeLocks = new ConcurrentDictionary<TcpClient, SemaphoreSlim>();

            //viewers
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

                //checking first message if its a viewer or messenger
                string? clientType = null;
                //loop while its heartbeat
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
                    //its a messenger
                    await HandleMessenger(client);
                }
                else 
                {
                    //its viewer
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

            //we send the message history
            await SendMessageHistoryMessage(client);

            //its a viewer, we just listen for pongs
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
                    messagesSoFar += message.Length + message;
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
            //we wait until we receive a valid username
            do
            {
                int messageType = await NetworkHelper.GetMessageType(client);
                lastHeard[client] = DateTime.UtcNow;
                int messageLength = await NetworkHelper.GetMessageLength(client);
                string message = await NetworkHelper.GetMessage(client, messageLength);
                if (messageType == 0)
                {
                    //its not a heartbeat(0: normal message, 1 denotes heartbeat)
                    username = message;
                    if (usernameToClient.ContainsKey(username))
                    {
                        await SendMessageToClient(client, 0, "taken");
                    }
                }

            } while (username == null || usernameToClient.ContainsKey(username));
            //username is not taken

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

            //listening loop for the client
            while (true)
            {
                int messageType = await NetworkHelper.GetMessageType(client);
                lastHeard[client] = DateTime.UtcNow;
                int messageLength = await NetworkHelper.GetMessageLength(client);
                string message = await NetworkHelper.GetMessage(client, messageLength);

                if (messageType == 1) continue;
                //its not a heartbeat(0: normal message, 1 denotes heartbeat)

                //add it to the message history
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
                    List<TcpClient> snapShot;//we loop through a snapshot this way if we encounter an error we won't modify the list we are iterating over
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
                    //we disconnect
                    await CloseClient(client);
                    break;
                }
                await Task.Delay(heartBeatIntervalInSec * 1000);
                
            }
        }
        private async Task CloseClient(TcpClient client)
        {

            bool success = writeLocks.TryRemove(client, out _);
            if (success) //if check necessary for not printing "Client disconnected..." multiple times to the console
            {
                success = clientToUsername.TryRemove(client, out string username);
                if (success)//disconnection could still happen if no username was chosen
                {
                    usernameToClient.TryRemove(username, out _);
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
                }
                lastHeard.TryRemove(client, out _);
                
                Console.WriteLine($"Client disconnected: {client.Client.RemoteEndPoint?.ToString()}");
                
                client.Close();
            }
            else
            {
                //we failed(success was false) because client might be a viewer client in which case we need a different removal
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
