using Newtonsoft.Json;
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
                .WriteTo.File(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "player.log"),
                              fileSizeLimitBytes: 5 * 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 2)
                .CreateLogger();

            // only one player may own the tuner
            using (var single = new Mutex(true, "QOCAST-Player", out bool first))
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
                    MessageBox.Show(ex.Message, "QOCAST Player", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                MessageBox.Show(ex.Message, "QOCAST Player", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            Log.Information("Starting, receiver {Receiver}, control port {Port}", config.receiver_type, config.control_port);
            string before = JsonConvert.SerializeObject(config);

            using (var receiver = new ReceiverService(config))
            using (var window = new PlayerWindow(config))
            {
                window.StationName = () => receiver.Service?.ServiceName;
                var control = new ControlServer(receiver, config);
                try
                {
                    control.Start();
                }
                catch (SocketException ex)
                {
                    Log.Error("Control port " + config.control_port + ": " + ex.Message);
                    MessageBox.Show("Port " + config.control_port + " is already in use - is OpenTuner or another player running?\nClose it and start the player again.",
                                    "QOCAST Player", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }

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
                window.Shown += (s, e) => receiver.Start();
                Application.Run(window);

                control.Close();
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

        // null = hide the text (the picture is showing)
        private static string StatusText(ReceiverState state, PlayerConfig config)
        {
            switch (state)
            {
                case ReceiverState.Disconnected:
                    return config.receiver_type == "picotuner" ? "PicoTuner not found - plug it in"
                         : config.receiver_type == "minitiouner" ? "MiniTiouner not found - plug it in"
                         : "No tuner found - plug in a MiniTiouner or PicoTuner";
                case ReceiverState.Waiting:
                    return "Waiting for QOCAST";
                case ReceiverState.Tuning:
                    return "No signal";
                default:
                    return "Signal locked - waiting for picture";
            }
        }
    }
}
