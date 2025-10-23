using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Helpers;

namespace Client.Messenger
{
    internal class Client
    {
        int heartBeatIntervalInSec = 5;
        int heartBeatChecksLimit = 3;

        DateTime lastMessage;//for heartbeats, keeping track of when we last heard from the server

        TcpClient socket;
        IPEndPoint remoteEndPoint;

        SemaphoreSlim writeLock;//writeLock for concurrency, heartbeats and normal messages both write to the socket
        Channel<string> normalMessages; //channel for normal messages, read by the loop where we ask for a username and produced by the listening loop

        int connectionAliveFlag;

        public Client(IPAddress serverAddress, Int32 serverPort)
        {
            remoteEndPoint = new IPEndPoint(serverAddress, serverPort);
            socket = new TcpClient(new IPEndPoint(IPAddress.Any, 0));
            connectionAliveFlag = 0;
            writeLock = new SemaphoreSlim(1, 1);
            normalMessages = Channel.CreateUnbounded<string>();
        }
        public async Task Connect()
        {
            try
            {
                socket.Connect(remoteEndPoint);
                connectionAliveFlag = 1;
                Console.WriteLine("Welcome to group chat!");
                Console.WriteLine("Open a message viewer to see messages!");
                //first we indicate to the server that we are a messenger
                await SendMessage(0, "messenger");

                CheckHeartBeats();
                Listen();
                string response = "taken";
                do //beggining phase: we try to register a username
                {
                    Console.Write("Choose a username: ");
                    string username = Console.ReadLine();

                    await SendMessage(0, username);
                    bool canRead = await normalMessages.Reader.WaitToReadAsync();
                    if (canRead)
                    {
                        response = await normalMessages.Reader.ReadAsync();
                    }
                    Console.Clear();
                } while (response != "accepted" && Interlocked.CompareExchange(ref connectionAliveFlag, 1, 1) == 1);
                //username accepted we enter the listening loop
                
                //we listen for userinputs
                while (Interlocked.CompareExchange(ref connectionAliveFlag, 1, 1) == 1)
                {
                    Console.Clear();
                    Console.Write("Enter message: ");
                    string messageToSend = Console.ReadLine();
                    await SendMessage(0, messageToSend);
                }
            }
            catch (Exception ex)
            {
                await CloseClient();
            }

        }
        private async Task Listen()
        {
            while (Interlocked.CompareExchange(ref connectionAliveFlag, 1, 1) == 1)
            {
                int messageType = await NetworkHelper.GetMessageType(socket);
                lastMessage = DateTime.UtcNow;
                int messageLength = await NetworkHelper.GetMessageLength(socket);
                string message = await NetworkHelper.GetMessage(socket, messageLength);
                if (messageType == 1)//its not a heartbeat(0: normal message, 1 denotes heartbeat)
                {
                    //its a heartbeat we send back a heartbeat response
                    await SendMessage(1, "PONG");
                }
                else//message is a normal message we write to the channel
                {
                    await normalMessages.Writer.WriteAsync(message);
                }
            }
        }
        private async Task CheckHeartBeats()
        {
            int heartBeatChecksLeft = heartBeatChecksLimit;
            while (Interlocked.CompareExchange(ref connectionAliveFlag, 1, 1) == 1)
            {
                heartBeatChecksLeft--;
                if ((DateTime.UtcNow - lastMessage).TotalSeconds <= heartBeatIntervalInSec * 2.5 && heartBeatChecksLeft >= 0)
                {
                    heartBeatChecksLeft = heartBeatChecksLimit;
                }
                else if (heartBeatChecksLeft < 0)
                {
                    //we disconnect
                    await CloseClient();
                }
                if (Interlocked.CompareExchange(ref connectionAliveFlag, 1, 1) == 1)
                {
                    await Task.Delay(heartBeatIntervalInSec * 1000);
                }
            }
        }
        private async Task CloseClient()
        {
            if (Interlocked.Exchange(ref connectionAliveFlag, 0) == 1)
            {
                Console.WriteLine($"Server unavailble, client shutting down");
                socket.Close();
                await Task.Delay(500);
                Environment.Exit(0);
            }

        }
        private async Task SendMessage(int messageType, string message)
        {
            await writeLock.WaitAsync();
            try
            {
                await NetworkHelper.SendMessage(messageType, socket, message);
            }
            finally
            {
                writeLock.Release();
            }
        }
    }
}
