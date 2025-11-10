using System.Net.Sockets;
using System.Text;

namespace Helpers
{
    public static class NetworkHelper
    {
        public const int headerLength = 4;
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

            if (!BitConverter.IsLittleEndian)
            {
                payloadSize.Reverse();
            }
            byte[] send = new byte[messageInBytes.Length + 5];
            send[0] = (byte)messageType;
            payloadSize.CopyTo(send, 1);
            messageInBytes.CopyTo(send, 5);

            await networkStream.WriteAsync(send, 0, send.Length);
        }
        public static async Task<int> GetMessageLength(TcpClient tcpClient)
        {
            NetworkStream networkStream = tcpClient.GetStream();

            byte[] headerBuffer = new byte[headerLength];
            await networkStream.ReadExactlyAsync(headerBuffer, 0, headerLength);

            if (!BitConverter.IsLittleEndian)
            {
                headerBuffer.Reverse();
            }
            return BitConverter.ToInt32(headerBuffer, 0);
        }
        public static async Task<string> GetMessage(TcpClient tcpClient, int messageLength)
        {
            NetworkStream networkStream = tcpClient.GetStream();

            byte[] messageBuffer = new byte[messageLength];
            await networkStream.ReadExactlyAsync(messageBuffer, 0, messageBuffer.Length);

            return Encoding.UTF8.GetString(messageBuffer);
        }
    }
}
