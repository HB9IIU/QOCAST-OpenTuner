using System.Net;
using System.Net.Sockets;

namespace QocastPlayer
{
    // A copy of the received stream for OBS, sent to a local UDP port (OBS Media Source: udp://127.0.0.1:9100).
    // Nobody listening is no problem.
    public static class ObsForward
    {
        public const int Port = 9100;
        private const int Datagram = 1316;      // 7 transport stream packets

        private static readonly IPEndPoint Target = new IPEndPoint(IPAddress.Loopback, Port);
        private static readonly Socket Udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        public static void Send(byte[] data, int count)
        {
            try
            {
                for (int offset = 0; offset < count; offset += Datagram)
                    Udp.SendTo(data, offset, System.Math.Min(Datagram, count - offset), SocketFlags.None, Target);
            }
            catch (SocketException)
            {
            }
        }
    }
}
