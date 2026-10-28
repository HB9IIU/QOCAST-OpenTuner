using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using opentuner;
using Serilog;
using System;
using System.Threading;

namespace QocastPlayer.Video
{
    public sealed class MediaInfo
    {
        public string VideoCodec;
        public uint VideoWidth;
        public uint VideoHeight;
        public uint VideoSarNum;
        public uint VideoSarDen;
        public string AudioCodec;
        public uint AudioRate;
        public uint AudioChannels;
    }

    // VLC playing the received stream into the window. Same settings as OpenTuner's
    // MediaPlayers\VLC\VLCMediaPlayer.cs (DirectSound audio, software decoding).
    // Play/Stop only record what is wanted; one worker thread does the VLC calls in order,
    // so fast lock/unlock changes never overlap and only the latest wish is carried out.
    public sealed class VideoPlayer : IDisposable
    {
        private readonly CircularBuffer _stream;
        private readonly LibVLC _libVLC;
        private readonly MediaPlayer _player;
        private readonly Thread _worker;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly object _lock = new object();

        private bool _wantPlaying;
        private int _wantSession;       // +1 on every Play(): a new lock = a fresh start
        private bool _disposed;

        private Media _media;
        private StreamInput _input;
        private int _playingSession = -1;
        private int _volume;

        // picture on screen (true) or gone (false); raised on a VLC thread
        public event Action<bool> PictureChanged;

        public MediaInfo LastMediaInfo { get; private set; }

        // call on the UI thread
        public VideoPlayer(VideoView view, CircularBuffer stream, int volume)
        {
            _stream = stream;
            _volume = volume;

            Core.Initialize();
            _libVLC = new LibVLC("--aout=directsound");
            _player = new MediaPlayer(_libVLC) { EnableMouseInput = false, EnableKeyInput = false };
            _player.Vout += OnVout;
            _player.EncounteredError += (s, e) => Log.Warning("VLC: error " + _libVLC.LastLibVLCError);
            view.MediaPlayer = _player;

            _worker = new Thread(WorkerLoop) { Name = "Video", IsBackground = true };
            _worker.Start();
        }

        public void Play()
        {
            lock (_lock)
            {
                _wantPlaying = true;
                _wantSession++;
            }
            _wake.Set();
        }

        public void Stop()
        {
            lock (_lock)
                _wantPlaying = false;
            _wake.Set();
        }

        // 0..100, 0 = silent
        public void SetVolume(int volume)
        {
            _volume = volume;
            try { _player.Volume = volume; }
            catch (Exception ex) { Log.Debug("VLC volume: " + ex.Message); }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                _wake.WaitOne();

                bool wantPlaying;
                int wantSession;
                lock (_lock)
                {
                    if (_disposed)
                        break;
                    wantPlaying = _wantPlaying;
                    wantSession = _wantSession;
                }

                try
                {
                    if (!wantPlaying)
                    {
                        StopVlc();
                    }
                    else if (_playingSession != wantSession)
                    {
                        StopVlc();
                        StartVlc(wantSession);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Video: VLC command failed");
                }
            }

            StopVlc();
        }

        private void StartVlc(int session)
        {
            Log.Information("Video: play");
            _input = new StreamInput(_stream);
            _media = new Media(_libVLC, _input);
            _media.AddOption(new MediaConfiguration { EnableHardwareDecoding = false });
            _player.Play(_media);
            _playingSession = session;
        }

        private void StopVlc()
        {
            if (_media == null)
                return;

            Log.Information("Video: stop");
            _input.End();
            _player.Stop();
            _media.Dispose();
            _input.Dispose();
            _media = null;
            _input = null;
            _playingSession = -1;
            PictureChanged?.Invoke(false);
        }

        private void OnVout(object sender, MediaPlayerVoutEventArgs e)
        {
            if (e.Count == 0)
                return;

            // volume only takes effect while playing (same as OpenTuner)
            _player.Volume = _volume;

            var info = new MediaInfo();
            Media media = _media;
            if (media != null)
            {
                foreach (MediaTrack track in media.Tracks)
                {
                    switch (track.TrackType)
                    {
                        case TrackType.Audio:
                            info.AudioChannels = track.Data.Audio.Channels;
                            info.AudioCodec = media.CodecDescription(TrackType.Audio, track.Codec);
                            info.AudioRate = track.Data.Audio.Rate;
                            break;
                        case TrackType.Video:
                            info.VideoCodec = media.CodecDescription(TrackType.Video, track.Codec);
                            info.VideoWidth = track.Data.Video.Width;
                            info.VideoHeight = track.Data.Video.Height;
                            info.VideoSarNum = track.Data.Video.SarNum;
                            info.VideoSarDen = track.Data.Video.SarDen;
                            break;
                    }
                }
            }
            LastMediaInfo = info;
            Log.Information("Video: picture {Codec} {Width}x{Height}, audio {Audio}", info.VideoCodec, info.VideoWidth, info.VideoHeight, info.AudioCodec);
            PictureChanged?.Invoke(true);
        }

        public void Dispose()
        {
            lock (_lock)
                _disposed = true;
            _wake.Set();
            if (!_worker.Join(5000))
                Log.Warning("Video: worker did not stop in time");

            _player.Dispose();
            _libVLC.Dispose();
        }
    }
}
