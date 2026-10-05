using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;

namespace opentuner.ExtraFeatures.QocastStream
{
    // posts tuner 1 status to the Qocast once per second: POST http://<qocast>/api/rx/info (JSON).
    // runs in the background with a 1 s timeout; a send is skipped while the previous one is still running.
    // errors (Qocast off / unreachable) are ignored, logged at most once per minute. Never blocks OpenTuner.
    public class QocastRxInfoSender
    {
        private const int IntervalMs = 1000;
        private const int ErrorLogIntervalS = 60;
        private const int ResolveCacheS = 60;

        private static readonly HttpClient _http = CreateHttpClient();

        private readonly string _target;
        private readonly Func<QocastRxInfo> _get_info;
        private readonly Timer _timer;

        private int _in_flight = 0;
        private volatile bool _closed = false;

        private IPAddress _resolved_address = null;
        private DateTime _resolved_at = DateTime.MinValue;
        private DateTime _last_error_log = DateTime.MinValue;

        public QocastRxInfoSender(string Target, Func<QocastRxInfo> GetInfo)
        {
            _target = Target;
            _get_info = GetInfo;
            _timer = new Timer(Tick, null, IntervalMs, IntervalMs);

            Log.Warning("QocastStream: sending tuner 1 status to http://" + _target + "/api/rx/info every second");
        }

        public void Close()
        {
            _closed = true;
            _timer.Dispose();
        }

        private static HttpClient CreateHttpClient()
        {
            // no proxy: the Qocast is on the local network
            var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            client.Timeout = TimeSpan.FromSeconds(1);
            client.DefaultRequestHeaders.ExpectContinue = false;
            return client;
        }

        private void Tick(object state)
        {
            if (_closed)
                return;

            // skip if the previous send is still in flight
            if (Interlocked.CompareExchange(ref _in_flight, 1, 0) != 0)
                return;

            Task.Run(async () =>
            {
                try
                {
                    QocastRxInfo info = _get_info();
                    if (info == null)
                        return;

                    IPAddress address = GetAddress();
                    if (address == null)
                        throw new Exception("'" + _target + "' has no IPv4 address");

                    string json = JsonConvert.SerializeObject(info);

                    using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                    using (var response = await _http.PostAsync("http://" + address.ToString() + "/api/rx/info", content).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new Exception("HTTP " + ((int)response.StatusCode).ToString() + " " + response.ReasonPhrase);
                    }
                }
                catch (Exception ex)
                {
                    // forget the resolved address so a changed DHCP address is picked up
                    _resolved_address = null;
                    LogError(ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _in_flight, 0);
                }
            });
        }

        // hostname lookups (mDNS) can be slow, so cache the result for a minute
        private IPAddress GetAddress()
        {
            if (_resolved_address == null || (DateTime.UtcNow - _resolved_at).TotalSeconds > ResolveCacheS)
            {
                _resolved_address = QocastTarget.ResolveIPv4(_target);
                _resolved_at = DateTime.UtcNow;
            }

            return _resolved_address;
        }

        private void LogError(Exception ex)
        {
            if ((DateTime.UtcNow - _last_error_log).TotalSeconds < ErrorLogIntervalS)
                return;

            _last_error_log = DateTime.UtcNow;

            string message = (ex is TaskCanceledException) ? "timeout" : ex.GetBaseException().Message;
            Log.Warning("QocastStream: can't send tuner status to " + _target + ": " + message + " (next message in " + ErrorLogIntervalS.ToString() + " s at the earliest)");
        }
    }
}
