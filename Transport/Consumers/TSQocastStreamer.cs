using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using opentuner.MediaSources;
using opentuner.ExtraFeatures.QocastStream;
using Serilog;

namespace opentuner
{
    // Always-on "Qocast stream": re-encodes the TS of one tuner with ffmpeg (H.264/AAC, fragmented MP4)
    // and ffmpeg pushes the result over TCP to the Qocast Nano.
    // One ffmpeg run = one TCP connection = one stream session:
    //  - started when the tuner gains demod lock
    //  - stopped on lost lock or retune (new station may have a different codec / resolution)
    //  - retried every 5 s while locked if ffmpeg exits by itself (e.g. receiver not listening)
    // Completely independent of TSUdpStreamer. Nothing in here may crash or block OpenTuner.
    public class TSQocastStreamer
    {
        public CircularBuffer ts_data_queue = new CircularBuffer(GlobalDefines.CircularBufferStartingCapacity);

        private const int TSPacketSize = 188;
        private const int MaxChunkPackets = 256;
        private const int RetryDelayMs = 5000;
        private const int RetuneHoldoffMs = 1000;
        private const int WriteTimeoutMs = 3000;
        private const int MaxLoggedFfmpegLines = 100;

        private readonly string _ffmpeg_path;
        private readonly string _target_ip;
        private readonly int _target_port;
        private readonly int _id;

        private volatile bool _running = false;
        private volatile bool _locked = false;
        private volatile bool _retune_requested = false;

        private Thread _StreamThread = null;
        private volatile Process _ffmpeg = null;
        private Stream _ffmpeg_stdin = null;
        private int _ffmpeg_logged_lines = 0;

        public int ID { get { return _id; } }

        public TSQocastStreamer(QocastStreamSettings settings, int Id, OTSource TSSource)
        {
            _ffmpeg_path = settings.FfmpegPath;
            _target_ip = settings.TargetIp;
            _target_port = settings.TargetPort;
            _id = Id;

            // register for TS Stream
            TSSource.RegisterTSConsumer(Id, ts_data_queue);

            _running = true;
            _StreamThread = new Thread(worker_thread);
            _StreamThread.IsBackground = true;
            _StreamThread.Name = "QOCAST Stream";
            _StreamThread.Start();

            Log.Warning("QOCAST Stream: enabled for tuner " + (Id + 1).ToString() + " -> tcp://" + _target_ip + ":" + _target_port.ToString());
        }

        // called on every nim status update
        public void SetLocked(bool locked)
        {
            _locked = locked;
        }

        // called on frequency / symbol rate / rf input change
        public void Retune()
        {
            _retune_requested = true;
        }

        public void Close()
        {
            _running = false;

            try
            {
                if (_StreamThread != null && !_StreamThread.Join(WriteTimeoutMs + 3000))
                {
                    Log.Warning("QocastStream: worker thread did not stop in time, killing ffmpeg");
                    _ffmpeg?.Kill();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "QocastStream: error while closing");
            }
        }

        private void worker_thread()
        {
            bool ts_sync = false;
            DateTime next_start_allowed = DateTime.MinValue;
            byte[] chunk;

            try
            {
                while (_running)
                {
                    if (_retune_requested)
                    {
                        _retune_requested = false;
                        StopFfmpeg("retune");
                        ts_data_queue.Clear();
                        ts_sync = false;
                        next_start_allowed = DateTime.UtcNow.AddMilliseconds(RetuneHoldoffMs);
                        continue;
                    }

                    if (!_locked)
                    {
                        if (_ffmpeg != null)
                            StopFfmpeg("lock lost");

                        ts_data_queue.Clear();
                        ts_sync = false;
                        Thread.Sleep(100);
                        continue;
                    }

                    // locked, but no ffmpeg running
                    if (_ffmpeg == null)
                    {
                        if (DateTime.UtcNow < next_start_allowed)
                        {
                            ts_data_queue.Clear();
                            Thread.Sleep(100);
                            continue;
                        }

                        ts_data_queue.Clear();
                        ts_sync = false;

                        if (!StartFfmpeg())
                            next_start_allowed = DateTime.UtcNow.AddMilliseconds(RetryDelayMs);

                        continue;
                    }

                    // ffmpeg exited by itself (receiver not listening, connection dropped, killed by the write watchdog, ...)
                    if (_ffmpeg.HasExited)
                    {
                        StopFfmpeg("ffmpeg exited");
                        Log.Warning("QocastStream: retrying in " + (RetryDelayMs / 1000).ToString() + " s");
                        next_start_allowed = DateTime.UtcNow.AddMilliseconds(RetryDelayMs);
                        continue;
                    }

                    // throw away data until synced
                    if (!ts_sync)
                    {
                        int skipped = 0;
                        while (ts_data_queue.Count > 0 && ts_data_queue.TryPeek() != 0x47 && skipped < TSPacketSize * 4)
                        {
                            ts_data_queue.Dequeue();
                            skipped++;
                        }

                        if (ts_data_queue.Count > 0 && ts_data_queue.TryPeek() == 0x47)
                        {
                            Log.Information("QocastStream: TS Synced");
                            ts_sync = true;
                        }
                        else
                        {
                            Thread.Sleep(10);
                        }
                        continue;
                    }

                    int available_packets = ts_data_queue.Count / TSPacketSize;

                    if (available_packets == 0)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    chunk = ts_data_queue.DequeueBytes(Math.Min(available_packets, MaxChunkPackets) * TSPacketSize);

                    // only pass complete, aligned packets to ffmpeg
                    int valid = 0;
                    while (valid < chunk.Length && chunk[valid] == 0x47)
                        valid += TSPacketSize;

                    if (valid > chunk.Length)
                        valid = chunk.Length;

                    if (valid < chunk.Length)
                    {
                        Log.Information("QocastStream: TS Sync Lost");
                        ts_sync = false;
                    }

                    if (valid > 0)
                        WriteToFfmpeg(chunk, valid);
                }
            }
            catch (ThreadAbortException)
            {
                Thread.ResetAbort();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "QocastStream: worker thread failed, Qocast stream disabled until restart");
            }
            finally
            {
                StopFfmpeg("shutdown");
            }
        }

        // accepts an IP address or a hostname (e.g. 127.0.0.1); a hostname is looked up on every
        // session so a changed DHCP address is picked up.
        private string ResolveTarget()
        {
            IPAddress address;
            if (IPAddress.TryParse(_target_ip, out address))
                return address.ToString();

            try
            {
                IPAddress ipv4 = QocastTarget.ResolveIPv4(_target_ip);

                if (ipv4 == null)
                {
                    Log.Warning("QocastStream: '" + _target_ip + "' has no IPv4 address - retrying in " + (RetryDelayMs / 1000).ToString() + " s");
                    return null;
                }

                Log.Warning("QocastStream: '" + _target_ip + "' resolved to " + ipv4.ToString());
                return ipv4.ToString();
            }
            catch (Exception ex)
            {
                Log.Warning("QocastStream: can't resolve '" + _target_ip + "': " + ex.Message + " - retrying in " + (RetryDelayMs / 1000).ToString() + " s");
                return null;
            }
        }

        private bool StartFfmpeg()
        {
            string target_address = ResolveTarget();

            if (target_address == null)
                return false;

            string args =
                "-hide_banner -loglevel warning -fflags +genpts+discardcorrupt -f mpegts -i pipe:0 " +
                "-map 0:v:0 -map 0:a:0? " +
                "-vf \"scale=w=1280:h=720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=25\" " +
                "-c:v libx264 -preset veryfast -tune zerolatency -profile:v high -level 4.0 -pix_fmt yuv420p " +
                "-b:v 2500k -maxrate 3000k -bufsize 3000k -g 25 -keyint_min 25 -sc_threshold 0 " +
                "-c:a aac -b:a 128k -ar 48000 -ac 2 " +
                "-f mp4 -movflags empty_moov+default_base_moof+frag_keyframe " +
                "tcp://" + target_address + ":" + _target_port.ToString();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpeg_path,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                };

                var process = new Process();
                process.StartInfo = psi;
                process.EnableRaisingEvents = true;
                process.ErrorDataReceived += Ffmpeg_ErrorDataReceived;

                _ffmpeg_logged_lines = 0;
                process.Start();
                process.BeginErrorReadLine();

                _ffmpeg_stdin = process.StandardInput.BaseStream;
                _ffmpeg = process;

                Log.Warning("QocastStream: ffmpeg started (pid " + process.Id.ToString() + ") -> tcp://" + target_address + ":" + _target_port.ToString());
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("QocastStream: can't start ffmpeg '" + _ffmpeg_path + "': " + ex.Message + " - retrying in " + (RetryDelayMs / 1000).ToString() + " s");
                _ffmpeg = null;
                _ffmpeg_stdin = null;
                return false;
            }
        }

        private void Ffmpeg_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;

            int count = Interlocked.Increment(ref _ffmpeg_logged_lines);

            if (count <= MaxLoggedFfmpegLines)
                Log.Information("QocastStream ffmpeg: " + e.Data);
            else if (count == MaxLoggedFfmpegLines + 1)
                Log.Information("QocastStream ffmpeg: further messages suppressed for this session");
        }

        // pipe writes can block if ffmpeg stalls, so write with a timeout
        private void WriteToFfmpeg(byte[] data, int count)
        {
            Stream stdin = _ffmpeg_stdin;

            if (stdin == null)
                return;

            try
            {
                Task write = Task.Run(() =>
                {
                    stdin.Write(data, 0, count);
                    stdin.Flush();
                });

                if (!write.Wait(WriteTimeoutMs))
                {
                    Log.Warning("QocastStream: ffmpeg not accepting data for " + (WriteTimeoutMs / 1000).ToString() + " s (receiver not listening / not reachable?), killing it");
                    KillFfmpeg();
                }
            }
            catch (Exception)
            {
                // broken pipe: ffmpeg has exited, the HasExited check picks it up
            }
        }

        private void StopFfmpeg(string reason)
        {
            Process process = _ffmpeg;

            if (process == null)
                return;

            try
            {
                // closing stdin lets ffmpeg finish the current fragment and exit cleanly
                try { _ffmpeg_stdin?.Close(); } catch (Exception) { }

                if (!process.WaitForExit(2000))
                {
                    Log.Information("QocastStream: ffmpeg did not exit, killing it");
                    KillFfmpeg();
                    process.WaitForExit(1000);
                }

                string exit_code = process.HasExited ? process.ExitCode.ToString() : "unknown";
                Log.Warning("QocastStream: ffmpeg stopped (" + reason + "), exit code " + exit_code);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "QocastStream: error stopping ffmpeg");
            }
            finally
            {
                try { process.Dispose(); } catch (Exception) { }
                _ffmpeg = null;
                _ffmpeg_stdin = null;
            }
        }

        private void KillFfmpeg()
        {
            try
            {
                Process process = _ffmpeg;
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch (Exception ex)
            {
                Log.Warning("QocastStream: can't kill ffmpeg: " + ex.Message);
            }
        }
    }
}
