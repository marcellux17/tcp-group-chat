using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Helpers;

namespace Client.Viewer
{
    internal class Client
    {
        int heartBeatIntervalInSec = 5;
        int heartBeatChecksLimit = 3;

        DateTime lastMessage;
        TcpClient socket;
        IPEndPoint remoteEndPoint;

        int connectionAliveFlag;
        public Client(IPAddress serverAddress, Int32 serverPort)
        {
            remoteEndPoint = new IPEndPoint(serverAddress, serverPort);
            socket = new TcpClient(new IPEndPoint(IPAddress.Any, 0));
            connectionAliveFlag = 0;
        }
        public async Task Connect()
        {
            try
            {
                socket.Connect(remoteEndPoint);
                connectionAliveFlag = 1;
                CheckHeartBeats();
                //first we indicate to the server that we are a messenger
                await SendMessage(0, "viewer");

                //then we wait for message history to appear(first normal message from server)
                string? messageHistory = null;
                do
                {
                    int messageType = await NetworkHelper.GetMessageType(socket);
                    lastMessage = DateTime.UtcNow;
                    int messageLength = await NetworkHelper.GetMessageLength(socket);
                    string message = await NetworkHelper.GetMessage(socket, messageLength);
                    if (messageType == 0)
                    {
                        messageHistory = message;
                    }
                    else
                    {
                        await SendMessage(1, "PONG");
                    }
                } while (messageHistory == null);

                //we print message history
                PrintMessageHistory(messageHistory);

                //we just listen for messages
                await Listen();
            }
            catch (Exception ex)
            {
                await CloseClient();
            }

        }
        private void PrintMessageHistory(string messageHistory)
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

                    while (currentCharIndex <= currentMessageBeingProcessedEndCharIndex && currentCharIndex < messageHistory.Length)
                    {
                        currentMessageBeingProcessed += messageHistory[currentCharIndex];
                        currentCharIndex++;
                    }
                    Console.WriteLine(currentMessageBeingProcessed);
                } while (currentCharIndex < messageHistory.Length);

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
                if (messageType == 1)
                {
                    //its a heartbeat we send back a heartbeat response
                    await SendMessage(1, "PONG");
                }
                else//its a normal message we print it to the console
                {
                    Console.WriteLine(message);
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
                await Task.Delay(750);
                Environment.Exit(0);
            }

        }
        private async Task SendMessage(int messageType, string message)
        { 
           await NetworkHelper.SendMessage(messageType, socket, message);
            
        }
    }
}
