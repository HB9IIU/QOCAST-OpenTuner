using Newtonsoft.Json.Linq;
using opentuner;
using Serilog;
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace QocastPlayer
{
    // Player 2 for a PicoTuner: the Player owns the USB device and has both channels. Channel 2's stream
    // arrives here by UDP (127.0.0.1) and is tuned through the Player's API ("tuner": 2). Player 2 never
    // opens the PicoTuner itself.
    public sealed class Pico2Client : IDisposable
    {
        private const string Api = "http://127.0.0.1:8090/api/v1/";

        private readonly int _udpPort;
        private readonly Thread _poll;
        private readonly Thread _udpThread;
        private volatile bool _stop;
        private volatile bool _locked;
        private volatile bool _playerAnswers = true;
        private volatile bool _tuned;
        private double _merDb = double.NaN;
        private long _frequencyKhz;
        private long _symbolRate;
        private UdpClient _udp;
        private CircularBuffer _stream;

        // raised on a worker thread
        public event Action<bool> LockChanged;

        private Pico2Client(int udpPort)
        {
            _udpPort = udpPort;
            _poll = new Thread(PollLoop) { Name = "Player status", IsBackground = true };
            _udpThread = new Thread(UdpLoop) { Name = "Channel 2 stream", IsBackground = true };
        }

        public bool Locked { get { return _locked; } }
        public bool PlayerAnswers { get { return _playerAnswers; } }
        public bool Tuned { get { return _tuned; } }

        // null unless the Player runs and has a PicoTuner with two channels
        public static Pico2Client TryConnect()
        {
            try
            {
                JObject health = Get("health", 1500);
                if (health == null || (int?)health["channels"] != 2)
                    return null;
                int port = (int?)health["tuner2_udp_port"] ?? 5002;
                return new Pico2Client(port);
            }
            catch (Exception ex)
            {
                Log.Information("Player 2: no PicoTuner Player (" + ex.Message + ")");
                return null;
            }
        }

        // the stream goes into this buffer, which the video player reads
        public void Start(CircularBuffer stream)
        {
            _stream = stream;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, _udpPort));
            _udp.Client.ReceiveBufferSize = 1 << 20;
            _udpThread.Start();
            _poll.Start();
        }

        public void ClearStream()
        {
            _stream?.Clear();
        }

        // short text for the window's bottom bar
        public string BarText()
        {
            if (!_playerAnswers)
                return "The QOCAST Player does not answer";
            if (!_tuned)
                return "Tuner 2";
            string text = string.Format(CultureInfo.InvariantCulture, "Tuner 2 · SR {0} kS/s", _symbolRate);
            if (_locked && !double.IsNaN(_merDb))
                text += string.Format(CultureInfo.InvariantCulture, " · MER {0:0.0} dB", _merDb);
            return text;
        }

        // asks the Player to tune channel 2; false with a short reason when it refuses
        public bool Tune(long frequencyKhz, long symbolRate, out string error)
        {
            error = null;
            try
            {
                var body = new JObject
                {
                    ["request_id"] = Guid.NewGuid().ToString("N"),
                    ["tuner"] = 2,
                    ["frequency_khz"] = frequencyKhz,
                    ["symbol_rate_ksps"] = symbolRate
                };
                JObject answer = Post("tune", body.ToString(), 3000);
                if (answer != null && (bool?)answer["accepted"] == true)
                {
                    _frequencyKhz = frequencyKhz;
                    _symbolRate = symbolRate;
                    _tuned = true;
                    return true;
                }
                error = (string)answer?["error"] ?? "refused";
                return false;
            }
            catch (WebException ex)
            {
                error = ex.Message;
                try
                {
                    if (ex.Response != null)
                        using (var reader = new StreamReader(ex.Response.GetResponseStream()))
                            error = (string)JObject.Parse(reader.ReadToEnd())["error"] ?? error;
                }
                catch { }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void PollLoop()
        {
            while (!_stop)
            {
                try
                {
                    JObject status = Get("status", 1500);
                    JToken tuner2 = status?["tuner2"];
                    _playerAnswers = true;
                    bool locked = tuner2 != null && tuner2.Type == JTokenType.Object && (bool?)tuner2["locked"] == true;
                    double? mer = tuner2 != null && tuner2.Type == JTokenType.Object ? (double?)tuner2["mer_db"] : null;
                    _merDb = mer ?? double.NaN;
                    if (locked != _locked)
                    {
                        _locked = locked;
                        LockChanged?.Invoke(locked);
                    }
                }
                catch (Exception)
                {
                    _playerAnswers = false;
                    if (_locked)
                    {
                        _locked = false;
                        LockChanged?.Invoke(false);
                    }
                }
                Thread.Sleep(500);
            }
        }

        private void UdpLoop()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (!_stop)
            {
                try
                {
                    byte[] datagram = _udp.Receive(ref from);
                    CircularBuffer stream = _stream;
                    if (stream == null || !_locked)
                        continue;
                    for (int i = 0; i < datagram.Length; i++)
                        stream.Enqueue(datagram[i]);
                }
                catch (SocketException)
                {
                    if (_stop)
                        return;
                    Thread.Sleep(50);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private static JObject Get(string path, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(Api + path);
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                return JObject.Parse(reader.ReadToEnd());
        }

        private static JObject Post(string path, string json, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(Api + path);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            request.ContentLength = bytes.Length;
            using (Stream body = request.GetRequestStream())
                body.Write(bytes, 0, bytes.Length);
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
                return JObject.Parse(reader.ReadToEnd());
        }

        public void Dispose()
        {
            _stop = true;
            try { _udp?.Close(); } catch { }
        }
    }
}
