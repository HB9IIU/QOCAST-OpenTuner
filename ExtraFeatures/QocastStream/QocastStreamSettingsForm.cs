using System;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;

namespace opentuner.ExtraFeatures.QocastStream
{
    // built in code (no designer file) - only the Qocast IP / hostname is editable here,
    // port and ffmpeg path keep their defaults (can still be changed in qocast_stream_settings.json)
    public class QocastStreamSettingsForm : Form
    {
        private QocastStreamSettings _settings;

        private TextBox txtTargetIp = new TextBox();

        public QocastStreamSettingsForm(ref QocastStreamSettings Settings)
        {
            this._settings = Settings;

            Text = "HB9IIU QOCAST Stream Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);

            var layout = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
            };

            txtTargetIp.Width = 180;

            layout.Controls.Add(new Label { Text = "QOCAST IP or hostname:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 0, 0);
            layout.Controls.Add(txtTargetIp, 1, 0);

            var note = new Label
            {
                Text = "Applies the next time the source is connected.",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Margin = new Padding(3, 10, 3, 10),
            };
            layout.Controls.Add(note, 0, 1);
            layout.SetColumnSpan(note, 2);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            var btnCancel = new Button { Text = "Cancel", AutoSize = true };
            var btnSave = new Button { Text = "Save", AutoSize = true };
            btnCancel.Click += btnCancel_Click;
            btnSave.Click += btnSave_Click;
            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(btnSave);
            layout.Controls.Add(buttons, 0, 2);
            layout.SetColumnSpan(buttons, 2);

            Controls.Add(layout);
            AcceptButton = btnSave;
            CancelButton = btnCancel;

            txtTargetIp.Text = _settings.TargetIp;
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            IPAddress address;
            string target = txtTargetIp.Text.Trim();

            bool valid_ip = IPAddress.TryParse(target, out address) && address.AddressFamily == AddressFamily.InterNetwork && target.Split('.').Length == 4;
            bool valid_hostname = !valid_ip && Uri.CheckHostName(target) == UriHostNameType.Dns && target.Trim('.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Length > 0;

            if (!valid_ip && !valid_hostname)
            {
                MessageBox.Show("Please enter a valid IP address or hostname, e.g. 192.168.0.178 or 127.0.0.1");
                return;
            }

            _settings.TargetIp = valid_ip ? address.ToString() : target;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
