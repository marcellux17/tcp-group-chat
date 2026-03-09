using System.Net.Sockets;
using System.Text;

namespace Helpers
{
    public static class NetworkHelper
    {
        public const int HEADER_LENGTH = 4;
        public static async Task<int> GetMessageType(TcpClient tcpClient)
        {
            NetworkStream networkStream = tcpClient.GetStream();

            byte[] headerBuffer = new byte[1];
            await networkStream.ReadExactlyAsync(headerBuffer, 0, 1);
            return headerBuffer[0];
        }
        public static async Task SendMessage(int messageType, TcpClient tcpClient, string message)
        {
            NetworkStream networkStream = tcpClient.GetStream();

            byte[] messageInBytes = Encoding.UTF8.GetBytes(message);
            byte[] payloadSize = BitConverter.GetBytes(messageInBytes.Length);

            byte[] send = new byte[messageInBytes.Length + 5];
            send[0] = (byte)messageType;
            payloadSize.CopyTo(send, 1);
            messageInBytes.CopyTo(send, 5);

            await networkStream.WriteAsync(send, 0, send.Length);
        }
        public static async Task<int> GetMessageLength(TcpClient tcpClient)
        {
            NetworkStream networkStream = tcpClient.GetStream();

            byte[] headerBuffer = new byte[HEADER_LENGTH];
            await networkStream.ReadExactlyAsync(headerBuffer, 0, HEADER_LENGTH);

            return BitConverter.ToInt32(headerBuffer, 0);
        }
        public static async Task<string> GetMessage(TcpClient tcpClient, int messageLength)
        {
            NetworkStream networkStream = tcpClient.GetStream();

            byte[] messageBuffer = new byte[messageLength];
            await networkStream.ReadExactlyAsync(messageBuffer, 0, messageBuffer.Length);

            return Encoding.UTF8.GetString(messageBuffer);
        }
        public static string CreateMessageHistoryString(List<string> messageHistory)
        {
            string messagesSoFar = "";
            foreach (string message in messageHistory)
            {
                string messageWithDelimiter = "|" + message;
                messagesSoFar += messageWithDelimiter.Length + messageWithDelimiter;
            }
            return messagesSoFar;
        }
        public static List<string> ParseMessageHistoryString(string messageHistory)
        {
            List<string> messages = new List<string>();
            if (messageHistory == string.Empty) return messages;
            
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
                messages.Add(currentMessageBeingProcessed);
            } while (currentCharIndex < messageHistory.Length);
            
            return messages;
        }
    }
}
