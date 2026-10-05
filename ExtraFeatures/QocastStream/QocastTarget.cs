using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace opentuner.ExtraFeatures.QocastStream
{
    public static class QocastTarget
    {
        // accepts an IP address or a hostname (e.g. 127.0.0.1).
        // IPv4 is preferred because Windows lists the Qocast's IPv6 addresses first.
        // returns null if the name has no IPv4 address, throws if it can't be resolved at all.
        public static IPAddress ResolveIPv4(string target)
        {
            IPAddress address;
            if (IPAddress.TryParse(target, out address))
                return address;

            return Dns.GetHostAddresses(target).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
    }
}
