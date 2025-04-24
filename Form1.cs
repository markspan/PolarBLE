using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

using LSL;
using System.Reflection.PortableExecutable;
using Windows.Storage.Streams;


namespace PolarBLE
{
    /// <summary>
    /// Windows Forms application for discovering, connecting to, and streaming ECG data from a Polar H10 sensor over BLE using LSL (LabStreamingLayer).
    /// </summary>
    public partial class Form1 : Form
    {
        /// <summary>
        /// Bluetooth Low Energy advertisement watcher used for scanning nearby BLE devices.
        /// </summary>
        BluetoothLEAdvertisementWatcher watcher;

        /// <summary>
        /// Dictionary storing detected BLE devices, indexed by their Bluetooth address.
        /// </summary>
        Dictionary<ulong, BluetoothLEAdvertisementReceivedEventArgs> devices = new Dictionary<ulong, BluetoothLEAdvertisementReceivedEventArgs>();

        /// <summary>
        /// LSL outlet used to stream ECG and Acc data.
        /// </summary>
        StreamOutlet ecgOutlet;
        StreamOutlet accOutlet;

        /// <summary>
        /// Reference to the GATT characteristic used for receiving ECG data from the Polar H10.
        /// </summary>
        GattCharacteristic ecgChar;
        GattCharacteristic accChar;

        /// <summary>
        /// UUID for the Polar Measurement Data (PMD) control characteristic.
        /// </summary>
        private const string PMD_CONTROL = "FB005C81-02E7-F387-1CAD-8ACD2D8DF0C8";

        /// <summary>
        /// UUID for the Polar Measurement Data (PMD) data characteristic.
        /// </summary>
        private const string PMD_DATA = "FB005C82-02E7-F387-1CAD-8ACD2D8DF0C8";

        /// <summary>
        /// Byte array command used to initialize ECG streaming on the Polar H10 sensor.
        /// </summary>
        private static readonly byte[] ECG_WRITE =
        {
            0x02,       // [0] Start measurement command
            0x00, 0x00, // [1-2] Reserved or unused (typically 0x0000)
            0x01,       // [3] Measurement type: PMD (Physical Measurement Data)
            0x82, 0x00, // [4-5] Feature type: ECG (0x0082 in little-endian)
            0x01,       // [6] Resolution index: 0x01 → 16-bit resolution
            0x01,       // [7] Sample rate: 0x01 → 130 Hz
            0x0E, 0x00  // [8-9] Range index or frame type (0x000E is a bit mysterious; varies by firmware)
        };

        /// <summary>
        /// Byte array command used to initialize ACC streaming on the Polar H10 sensor.
        /// Range index 0x02 = ±2g
        /// Range index 0x04 = ±4g
        /// Range index 0x08 = ±8g
        /// </summary>
        private static readonly byte[] ACC_WRITE =
        {
            0x02,       // Start measurement
            0x02, 0x00, // Reserved
            0x01,       // Measurement type: PMD (0x01)
            0xC8, 0x00, // Feature type: ACC (0x0083 little-endian)
            0x01,       // Resolution index (0x01 = 16-bit)
            0x01,       // Sample rate (0x01 = 200Hz)
            0x10, 0x00, // Range index (optional; this sets ±4g in some docs)
            0x02, 0x01, 0x08, 0x00 // Additional configuration bytes
        };

        private const string BatteryLevelCharacteristicUuid = "00002A19-0000-1000-8000-00805F9B34FB";

        /// <summary>
        /// Internal counter for cycling the "Streaming" status animation dots.
        /// </summary>
        private int dotState = 0;
        private TextProgressBar? Battery = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="Form1"/> class and sets up event handlers.
        /// </summary>
        public Form1()
        {
            InitializeComponent();
            Battery = new TextProgressBar()
            {
                Size = new Size(100, 23),
                Location = new Point(223, 131),
                Visible = false,
                Value = 0,
            };
            this.Controls.Add(Battery);
            listBoxDevices.SelectedIndexChanged += ListBoxDevices_SelectedIndexChanged;
        }

        private void CustomDrawProgressBar(PaintEventArgs e)
        {
            int progress = 60; // Your progress value
            int max = 100;

            float percent = (float)progress / max;
            int width = (int)(this.Width * percent);

            // Draw background
            e.Graphics.FillRectangle(Brushes.Gray, 0, 0, this.Width, this.Height);
            // Draw progress
            e.Graphics.FillRectangle(Brushes.Green, 0, 0, width, this.Height);
            // Draw text
            string text = $"{progress}%";
            var size = e.Graphics.MeasureString(text, this.Font);
            var textPos = new PointF(
                (this.Width - size.Width) / 2,
                (this.Height - size.Height) / 2
            );
            e.Graphics.DrawString(text, this.Font, Brushes.White, textPos);
        }


        /// <summary>
        /// Event handler for the Scan button click event. Starts scanning for nearby Polar H10 BLE devices.
        /// </summary>
        /// <param name="sender">The button object that triggered the event.</param>
        /// <param name="e">Event arguments associated with the click event.</param>
        private void btnScan_Click(object sender, EventArgs e)
        {
            devices.Clear();
            listBoxDevices.Items.Clear();
            var selected = listBoxDevices.SelectedItem?.ToString();
            watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            watcher.Received += (w, args) =>
            {
                if (args.Advertisement.LocalName.Contains("Polar H10") &&
                    !devices.ContainsKey(args.BluetoothAddress))
                {
                    devices[args.BluetoothAddress] = args;
                    BeginInvoke(() =>
                        listBoxDevices.Items.Add($"{args.Advertisement.LocalName} [{args.BluetoothAddress}]"));
                }
            };

            watcher.Start();
            lblStatus.Text = "Scanning...";
        }

        /// <summary>
        /// Event handler triggered when a user selects a BLE device from the list. Connects to the device and starts ECG data streaming.
        /// </summary>
        /// <param name="sender">The ListBox control triggering the event.</param>
        /// <param name="e">Event arguments associated with the selection event.</param>
        private async void ListBoxDevices_SelectedIndexChanged(object? sender, EventArgs e)
        {
            watcher?.Stop();
            lblStatus.Text = "Connecting...";

            var selected = listBoxDevices.SelectedItem?.ToString();
            if (selected == null) return;

            var address = ulong.Parse(selected.Split('[')[1].TrimEnd(']'));
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

            var services = await device.GetGattServicesAsync();
            foreach (var service in services.Services)
            {
                var chars = await service.GetCharacteristicsAsync();
                foreach (var c in chars.Characteristics)
                {
                    if (c == null) continue;
                    if (c?.Uuid.ToString().ToUpper() == PMD_CONTROL)
                    {
                        await c.WriteValueAsync(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(ECG_WRITE));
                        await c.WriteValueAsync(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(ACC_WRITE));
                    }

                    if (c?.Uuid.ToString().ToUpper() == PMD_DATA)
                    {
                        ecgChar = c;
                        ecgChar.ValueChanged += EcgChar_ValueChanged;
                        await ecgChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.Notify);

                        accChar = c;
                        accChar.ValueChanged += AccChar_ValueChanged;
                        await accChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    }
                    if (c?.Uuid == new Guid(BatteryLevelCharacteristicUuid))
                    {
                        // Read the value
                        var readResult = await c?.ReadValueAsync(BluetoothCacheMode.Uncached);
                        if (readResult.Status == GattCommunicationStatus.Success)
                        {
                            var reader = DataReader.FromBuffer(readResult.Value);
                            byte[] data = new byte[readResult.Value.Length];
                            reader.ReadBytes(data);

                            // Use the input byte array
                            if (Battery != null)
                            {
                                Battery.Value = data[0];
                            }   
                        }
                    }
                }
            }

            if (Battery != null)  Battery.Visible = true;
            StartLSL(device.Name, address.ToString());
            lblStatus.Text = "Wait for Streaming...";
        }

        /// <summary>
        /// Callback invoked when ECG characteristic receives new data. Parses ECG samples and pushes them to the LSL stream.
        /// </summary>
        /// <param name="sender">The GATT characteristic that triggered the event.</param>
        /// <param name="args">Event arguments containing the characteristic value data.</param>
        private unsafe void EcgChar_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
            reader.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
            byte[] data = new byte[args.CharacteristicValue.Length];
            reader.ReadBytes(data);

            if (data[0] != 0x00) return;

            int step = 3;
            data = data.Skip(10).ToArray();
            int offset = 0;
            // Process the data in chunks of 3 bytes
            while (offset < data.Length)
            {
                int raw = (data[offset]) | (data[offset + 1] << 8) | (data[offset + 2] << 16);
                if ((raw & 0x800000) != 0)  // if sign bit (bit 23) is set
                    raw |= unchecked((int)0xFF000000);  // sign-extend to 32 bits

                ecgOutlet?.push_sample(new float[] { raw });
                offset += step;
            }
            // Animate "Streaming" label with dots to show activity
            dotState = (dotState + 1) % 4;
            string dots = new string('.', dotState);
            BeginInvoke(() => lblStatus.Text = $"Streaming{dots}");
        }

        /// <summary>
        /// Callback invoked when ACC characteristic receives new data. Parses ACC samples and pushes them to the LSL stream.
        /// </summary>
        /// <param name="sender">The GATT characteristic that triggered the event.</param>
        /// <param name="args">Event arguments containing the characteristic value data.</param>
        private void AccChar_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
            reader.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
            byte[] data = new byte[args.CharacteristicValue.Length];
            reader.ReadBytes(data);

            if (data.Length < 10 || data[0] != 0x02) return;

            int resolution = 16;// data[4]; // usually 16 bits
            int bytesPerAxis = resolution / 8;
            int bytesPerSample = 3 * bytesPerAxis;

            data = data.Skip(10).ToArray(); // skip header

            for (int i = 0; i + bytesPerSample <= data.Length; i += bytesPerSample)
            {
                short x = BitConverter.ToInt16(data, i);
                short y = BitConverter.ToInt16(data, i + 2);
                short z = BitConverter.ToInt16(data, i + 4);

                accOutlet?.push_sample(new float[] { x, y, z });
            }
            dotState = (dotState + 1) % 4;
            string dots = new string('.', dotState);
            BeginInvoke(() => lblStatus.ForeColor = Color.Green);
        }

        /// <summary>
        /// Initializes and starts the LSL (LabStreamingLayer) outlet stream for ECG data.
        /// </summary>
        /// <param name="name">The name of the device used as the stream name.</param>
        /// <param name="id">A unique identifier for the stream based on the Bluetooth address.</param>
        private void StartLSL(string name, string id)
        {
            var info = new StreamInfo(name + "_ecg", "ECG", 1, 130, channel_format_t.cf_float32, id = name + "_ecg");
            var channels = info.desc().append_child("channels");
            channels.append_child("channel")
                .append_child_value("name", "ECG")
                .append_child_value("unit", "microvolts")
                .append_child_value("type", "ECG");

            ecgOutlet = new StreamOutlet(info, 74, 360);

            var accInfo = new StreamInfo(name + "_acc", "Accelerometer", 3, 200, channel_format_t.cf_float32, id = name + "_acc");
            var accChannels = accInfo.desc().append_child("channels");
            accChannels.append_child("channel").append_child_value("name", "X");
            accChannels.append_child("channel").append_child_value("name", "Y");
            accChannels.append_child("channel").append_child_value("name", "Z");
            accOutlet = new StreamOutlet(accInfo, 25, 360);
        }
    }
}
