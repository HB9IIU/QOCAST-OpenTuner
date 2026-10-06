using LibVLCSharp.Shared;
using opentuner;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace QocastPlayer.Video
{
    // Feeds the received transport stream to VLC. Based on OpenTuner's MediaPlayers\VLC\TSStreamMediaInput.cs;
    // starts at a TS sync byte without passing filler bytes on, and never ends by itself (only End()).
    public sealed class StreamInput : MediaInput
    {
        private const byte TsSync = 0x47;
        private const int MinBytes = 1010;      // same wait threshold as OpenTuner

        private readonly CircularBuffer _stream;
        private volatile bool _end;
        private bool _synced;

        public StreamInput(CircularBuffer stream)
        {
            _stream = stream;
            CanSeek = false;
        }

        // makes the waiting Read return, so VLC can stop
        public void End()
        {
            _end = true;
        }

        public override bool Open(out ulong size)
        {
            size = ulong.MaxValue;
            return true;
        }

        public override void Close()
        {
        }

        public override int Read(IntPtr buf, uint len)
        {
            while (true)
            {
                while (_stream.Count < MinBytes)
                {
                    if (_end)
                        return 0;
                    Thread.Sleep(5);
                }
                if (_end)
                    return 0;

                byte[] data = _stream.DequeueBytes((int)Math.Min(len, (uint)_stream.Count));

                int start = 0;
                if (!_synced)
                {
                    start = Array.IndexOf(data, TsSync);
                    if (start < 0)
                        continue;
                    _synced = true;
                }

                int count = data.Length - start;
                Marshal.Copy(data, start, buf, count);
                return count;
            }
        }

        public override bool Seek(ulong offset)
        {
            return false;
        }
    }
}
