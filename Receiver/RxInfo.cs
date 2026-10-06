using opentuner;
using opentuner.ExtraFeatures.QocastStream;
using System;

namespace QocastPlayer.Receiver
{
    // Tuner 1 status for QOCAST (/status "receiver" and later /api/rx/info), same values as
    // OpenTuner's MinitiounerQocastInfo.cs fills from its Properties panel (MinitiounerProperties.cs).
    public static class RxInfo
    {
        public static QocastRxInfo Build(ReceiverService receiver, PlayerConfig config)
        {
            var info = new QocastRxInfo { hardware_interface = receiver.Connected ? receiver.HardwareName : null };
            TunerStatus status = receiver.LastStatus;
            TuneRequest request = receiver.LastRequest;

            info.freq_offset_khz = config.lnb_offset_khz;
            info.lnb_a_power = config.rf_input == "A" ? LnbPowerText(config.lnb_power) : "OFF";
            info.lnb_b_power = config.rf_input == "B" ? LnbPowerText(config.lnb_power) : "OFF";
            if (request != null)
            {
                info.requested_freq_khz = request.RfKhz;
                info.if_freq_khz = request.IfKhz;
            }

            if (status == null || !receiver.Connected)
                return info;

            double mer = Convert.ToDouble(status.T1P2_mer) / 10;
            string modcod = null;
            double? dbMargin = null;
            try
            {
                switch (status.T1P2_demod_status)
                {
                    case 2:
                        modcod = lookups.modcod_lookup_dvbs2[status.T1P2_modcode];
                        dbMargin = Math.Round(mer - lookups.modcod_lookup_dvbs2_threshold[status.T1P2_modcode], 1);
                        break;
                    case 3:
                        modcod = lookups.modcod_lookup_dvbs[status.T1P2_modcode];
                        dbMargin = Math.Round(mer - lookups.modcod_lookup_dvbs_threshold[status.T1P2_modcode], 1);
                        break;
                }
            }
            catch (Exception)
            {
                // unknown modcod number: leave modcod and margin empty, like OpenTuner
            }

            info.locked = receiver.State == ReceiverState.Locked;
            info.demod_state = lookups.demod_state_lookup[status.T1P2_demod_status];
            info.db_margin = dbMargin;
            info.mer_db = mer;
            info.rf_level_db = status.T1P2_input_power_level;
            info.rf_input = status.T1P2_rf_input == 1 ? "A" : "B";
            info.symbol_rate_ksps = status.T1P2_symbol_rate / 1000;
            info.modcod = modcod;
            info.lna_gain = status.T1P2_lna_gain;
            info.ber = status.T1P2_ber;
            info.carrier_offset_hz = status.T1P2_frequency_carrier_offset;
            info.lpdc_errors = status.errors_ldpc_count;
            if (info.locked)
            {
                info.stream_format = lookups.stream_format_lookups[Convert.ToInt32(status.T1P2_stream_format)].ToString();
                TSStatus service = receiver.Service;
                if (service != null)
                {
                    info.service_name = service.ServiceName;
                    info.service_provider = service.ServiceProvider;
                    info.null_packets_percent = service.NullPacketsPerc;
                }
            }

            return info;
        }

        // same texts as the OpenTuner Properties panel
        private static string LnbPowerText(string lnbPower)
        {
            switch (lnbPower)
            {
                case "vertical": return "Vertical (12V)";
                case "horizontal": return "Horizontal (18V)";
                default: return "OFF";
            }
        }
    }
}
