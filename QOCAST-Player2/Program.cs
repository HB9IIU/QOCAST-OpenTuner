using Newtonsoft.Json;
using opentuner;
using QocastPlayer.Control;
using QocastPlayer.Receiver;
using QocastPlayer.Video;
using Serilog;
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace QocastPlayer
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "player2.log"),
                              fileSizeLimitBytes: 5 * 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 2)
                .CreateLogger();

            // only one player may own the tuner
            using (var single = new Mutex(true, "QOCAST-Player2", out bool first))
            {
                if (!first)
                {
                    Log.Information("Already running, exiting");
                    return 2;
                }

                try
                {
                    return Run(args);
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Player stopped");
                    MessageBox.Show(ex.Message, "QOCAST Player 2", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally
                {
                    Log.CloseAndFlush();
                }
            }
        }

        private static int Run(string[] args)
        {
            string path = PlayerConfig.DefaultPath;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--config")
                    path = Path.GetFullPath(args[i + 1]);
            }

            PlayerConfig config;
            try
            {
                config = PlayerConfig.Load(path);
            }
            catch (ConfigException ex)
            {
                Log.Error("Config: " + ex.Message);
                MessageBox.Show(ex.Message, "QOCAST Player 2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            Log.Information("Starting, receiver {Receiver}, control port {Port}", config.receiver_type, config.control_port);
            string before = JsonConvert.SerializeObject(config);

            // A PicoTuner with two channels, owned by the QOCAST Player: Player 2 is channel 2's window
            // (the stream comes by UDP, tuning goes through the Player). It never opens the USB device.
            Pico2Client pico = Pico2Client.TryConnect();
            if (pico != null)
                return RunPicoClient(config, pico, before);

            using (var receiver = new ReceiverService(config))
            using (var window = new PlayerWindow(config))
            {
                window.StationName = () => RxInfo.BarText(receiver, config);
                var control = new ControlServer(receiver, config);
                try
                {
                    control.Start();
                }
                catch (SocketException ex)
                {
                    Log.Error("Control port " + config.control_port + ": " + ex.Message);
                    MessageBox.Show("Port " + config.control_port + " is already in use - is OpenTuner or another player running?\nClose it and start the player again.",
                                    "QOCAST Player 2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }

                // the BATC spectrum at the bottom: a click on a signal tunes the receiver to it
                var feed = new SpectrumFeed();
                feed.FrameReceived += frame => window.BeginInvoke((Action)(() => window.Spectrum.SetFrame(frame)));
                feed.ConnectionChanged += ok => window.BeginInvoke((Action)(() => window.Spectrum.SetConnected(ok)));
                window.Spectrum.SpectrumClicked += mhz =>
                {
                    ushort[] frame = window.Spectrum.Frame;
                    FoundSignal? signal = frame == null ? (FoundSignal?)null : SignalFinder.Find(frame, mhz);
                    if (signal == null)
                    {
                        window.Spectrum.ShowMessage(string.Format("No signal at {0:0.000} MHz", mhz));
                        return;
                    }
                    long khz = (long)Math.Round(signal.Value.CenterMhz * 1000);
                    string error = receiver.CheckTune(khz, signal.Value.SymbolRate);
                    if (error != null)
                    {
                        window.Spectrum.ShowMessage("Not tuned: " + error);
                        return;
                    }
                    receiver.Tune(khz, signal.Value.SymbolRate);
                    window.Spectrum.SetTuned(signal.Value.CenterMhz, signal.Value.SymbolRate);
                    window.Spectrum.ShowMessage(string.Format("Tuning {0:0.000} MHz - {1} kS/s", signal.Value.CenterMhz, signal.Value.SymbolRate));
                };
                window.Shown += (s, e) => feed.Start();

                var video = new VideoPlayer(window.Video, receiver.Stream, config.muted ? 0 : config.volume);
                // until Windows has the player's sound, VLC's own volume; then Windows (the VU meter stays alive when muted)
                window.SoundChanged += (muted, volume) => video.SetVolume(window.WindowsSound ? 100 : muted ? 0 : volume);

                // locked = play the stream, anything else = stop (no frozen picture left on screen)
                receiver.StateChanged += state =>
                {
                    if (state == ReceiverState.Locked)
                        video.Play();
                    else
                        video.Stop();
                    window.ShowStatus(StatusText(state, config));
                };
                video.PictureChanged += picture =>
                {
                    if (picture)
                    {
                        MediaInfo info = video.LastMediaInfo;
                        if (info != null)
                            window.SetVideoAspectRatio(info.VideoWidth, info.VideoHeight,
                                                       info.VideoSarNum, info.VideoSarDen);
                    }
                    window.ShowStatus(picture ? null : StatusText(receiver.State, config));
                };

                window.ShowStatus(StatusText(ReceiverState.Disconnected, config));
                window.Shown += (s, e) =>
                {
                    receiver.Start();
                    // opens on the beacon: a signal that is always there, to tune from
                    receiver.Tune((long)Math.Round(Band.BeaconMhz * 1000), 1500);
                    window.Spectrum.SetTuned(Band.BeaconMhz, 1500);
                };
                Application.Run(window);

                control.Close();
                feed.Dispose();
                video.Dispose();
            }

            // save only what the user changed (window place, sound, always on top)
            if (JsonConvert.SerializeObject(config) != before)
            {
                try { config.Save(); }
                catch (Exception ex) { Log.Warning("Could not save " + PlayerConfig.FileName + ": " + ex.Message); }
            }

            Log.Information("Bye");
            return 0;
        }

        private static int RunPicoClient(PlayerConfig config, Pico2Client pico, string before)
        {
            Log.Information("PicoTuner with two channels: Player 2 shows channel 2");
            using (var window = new PlayerWindow(config))
            {
                var stream = new CircularBuffer(GlobalDefines.CircularBufferStartingCapacity);
                window.StationName = () => pico.BarText();
                var video = new VideoPlayer(window.Video, stream, config.muted ? 0 : config.volume);
                window.SoundChanged += (muted, volume) => video.SetVolume(window.WindowsSound ? 100 : muted ? 0 : volume);

                Func<string> idleText = () => !pico.PlayerAnswers ? "The QOCAST Player does not answer"
                    : !pico.Tuned ? "Click a signal in the spectrum" : "No signal";

                pico.LockChanged += locked =>
                {
                    pico.ClearStream();
                    if (locked)
                        video.Play();
                    else
                        video.Stop();
                    window.ShowStatus(locked ? "Signal locked - waiting for picture" : idleText());
                };
                video.PictureChanged += picture =>
                {
                    if (picture)
                    {
                        MediaInfo info = video.LastMediaInfo;
                        if (info != null)
                            window.SetVideoAspectRatio(info.VideoWidth, info.VideoHeight,
                                                       info.VideoSarNum, info.VideoSarDen);
                    }
                    window.ShowStatus(picture ? null : (pico.Locked ? "Signal locked - waiting for picture" : idleText()));
                };

                var feed = new SpectrumFeed();
                feed.FrameReceived += frame => window.BeginInvoke((Action)(() => window.Spectrum.SetFrame(frame)));
                feed.ConnectionChanged += ok => window.BeginInvoke((Action)(() => window.Spectrum.SetConnected(ok)));

                Action<double, int> tune = (centerMhz, symbolRate) =>
                {
                    string error;
                    if (pico.Tune((long)Math.Round(centerMhz * 1000), symbolRate, out error))
                    {
                        window.Spectrum.SetTuned(centerMhz, symbolRate);
                        window.Spectrum.ShowMessage(string.Format("Tuning {0:0.000} MHz - {1} kS/s", centerMhz, symbolRate));
                        window.ShowStatus("No signal");
                    }
                    else
                    {
                        window.Spectrum.ShowMessage("Not tuned: " + error);
                    }
                };
                window.Spectrum.SpectrumClicked += mhz =>
                {
                    ushort[] frame = window.Spectrum.Frame;
                    FoundSignal? signal = frame == null ? (FoundSignal?)null : SignalFinder.Find(frame, mhz);
                    if (signal == null)
                    {
                        window.Spectrum.ShowMessage(string.Format("No signal at {0:0.000} MHz", mhz));
                        return;
                    }
                    tune(signal.Value.CenterMhz, signal.Value.SymbolRate);
                };

                window.ShowStatus("Click a signal in the spectrum");
                window.Shown += (s, e) =>
                {
                    pico.Start(stream);
                    feed.Start();
                    // opens on the beacon, as Player 2 does with a MiniTiouner
                    tune(Band.BeaconMhz, 1500);
                };
                Application.Run(window);

                feed.Dispose();
                pico.Dispose();
                video.Dispose();
            }

            if (JsonConvert.SerializeObject(config) != before)
            {
                try { config.Save(); }
                catch (Exception ex) { Log.Warning("Could not save " + PlayerConfig.FileName + ": " + ex.Message); }
            }
            Log.Information("Bye");
            return 0;
        }

        // null = hide the text (the picture is showing)
        private static string StatusText(ReceiverState state, PlayerConfig config)
        {
            switch (state)
            {
                case ReceiverState.Disconnected:
                    if (System.Diagnostics.Process.GetProcessesByName("opentuner").Length > 0)
                        return "OpenTuner is using the tuner - please close OpenTuner";
                    return config.receiver_type == "picotuner" ? "PicoTuner not found - plug it in"
                         : config.receiver_type == "minitiouner" ? "MiniTiouner not found - plug it in"
                         : "No tuner found - plug in a MiniTiouner or PicoTuner";
                case ReceiverState.Waiting:
                    return "Click a signal in the spectrum";
                case ReceiverState.Tuning:
                    return "No signal";
                default:
                    return "Signal locked - waiting for picture";
            }
        }
    }
}
