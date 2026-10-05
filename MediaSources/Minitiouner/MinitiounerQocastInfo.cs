using System;
using opentuner.ExtraFeatures.QocastStream;

namespace opentuner.MediaSources.Minitiouner
{
    // tuner 1 status for the Qocast (see QocastRxInfoSender). Filled from the same data and at the same
    // places as the Properties panel: nim status, ts parser status and media player status.
    // Those arrive on different threads, hence the lock.
    public partial class MinitiounerSource
    {
        private readonly object _qocast_info_lock = new object();
        private readonly QocastRxInfo _qocast_info = new QocastRxInfo();

        public QocastRxInfo GetQocastRxInfo()
        {
            lock (_qocast_info_lock)
            {
                _qocast_info.hardware_interface = hardware_interface?.GetName;
                return _qocast_info.Clone();
            }
        }

        private void UpdateQocastInfo(TunerStatus status, string demod_state, double mer, double? db_margin, string modcod, string stream_format)
        {
            bool locked = status.T1P2_demod_status > 1;

            lock (_qocast_info_lock)
            {
                _qocast_info.locked = locked;
                _qocast_info.demod_state = demod_state;
                _qocast_info.db_margin = db_margin.HasValue ? Math.Round(db_margin.Value, 1) : (double?)null;
                _qocast_info.mer_db = mer;
                _qocast_info.rf_level_db = status.T1P2_input_power_level;
                _qocast_info.rf_input = (status.T1P2_rf_input == 1 ? "A" : "B");
                _qocast_info.requested_freq_khz = GetFrequency(0, true);
                _qocast_info.if_freq_khz = GetFrequency(0, false);
                _qocast_info.symbol_rate_ksps = status.T1P2_symbol_rate / 1000;
                _qocast_info.freq_offset_khz = current_offset_0;
                _qocast_info.modcod = (modcod == "Unknown" ? null : modcod);
                _qocast_info.lna_gain = status.T1P2_lna_gain;
                _qocast_info.ber = status.T1P2_ber;
                _qocast_info.carrier_offset_hz = status.T1P2_frequency_carrier_offset;
                _qocast_info.lpdc_errors = status.errors_ldpc_count;
                _qocast_info.lnb_a_power = LnbPowerText(current_lnba_psu);
                _qocast_info.lnb_b_power = LnbPowerText(current_lnbb_psu);

                if (locked)
                {
                    _qocast_info.stream_format = stream_format;
                }
                else
                {
                    // the panel clears the ts / media values when not locked, do the same
                    _qocast_info.stream_format = null;
                    _qocast_info.service_name = null;
                    _qocast_info.service_provider = null;
                    _qocast_info.null_packets_percent = null;
                    _qocast_info.video_codec = null;
                    _qocast_info.video_resolution = null;
                    _qocast_info.audio_codec = null;
                    _qocast_info.audio_rate = null;
                }
            }
        }

        private void UpdateQocastInfo(TSStatus ts_status)
        {
            lock (_qocast_info_lock)
            {
                _qocast_info.service_name = ts_status.ServiceName;
                _qocast_info.service_provider = ts_status.ServiceProvider;
                _qocast_info.null_packets_percent = ts_status.NullPacketsPerc;
            }
        }

        private void UpdateQocastInfo(MediaStatus media_status)
        {
            lock (_qocast_info_lock)
            {
                _qocast_info.video_codec = media_status.VideoCodec;
                _qocast_info.video_resolution = media_status.VideoWidth.ToString() + "x" + media_status.VideoHeight.ToString();
                _qocast_info.audio_codec = media_status.AudioCodec;
                _qocast_info.audio_rate = media_status.AudioRate.ToString() + " Hz, " + media_status.AudioChannels.ToString() + " channels";
            }
        }

        // same texts as the Properties panel
        private static string LnbPowerText(byte psu)
        {
            switch (psu)
            {
                case 0: return "OFF";
                case 1: return "Vertical (12V)";
                case 2: return "Horizontal (18V)";
                default: return null;
            }
        }
    }
}
