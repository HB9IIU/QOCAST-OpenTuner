namespace opentuner.ExtraFeatures.QocastStream
{
    public class QocastStreamSettings
    {
        public string TargetIp = "127.0.0.1";
        public int TargetPort = 5001;

        // Resolved to OpenTuner's bundled ffmpeg at source connection time.
        public string FfmpegPath = "ffmpeg\\ffmpeg.exe";
    }
}
