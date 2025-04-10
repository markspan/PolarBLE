using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using LSL;

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
        Dictionary<ulong, BluetoothLEAdvertisementReceivedEventArgs> devices = [];

        /// <summary>
        /// LSL outlet used to stream ECG data.
        /// </summary>
        StreamOutlet outlet;

        /// <summary>
        /// Reference to the GATT characteristic used for receiving ECG data from the Polar H10.
        /// </summary>
        GattCharacteristic ecgChar;

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
        private static readonly byte[] ECG_WRITE = new byte[]
        {
            0x02, 0x00, 0x00, 0x01, 0x82, 0x00, 0x01, 0x01, 0x0E, 0x00
        };
        /// <summary>
        /// Internal counter for cycling the "Streaming" status animation dots.
        /// </summary>
        private int dotState = 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="Form1"/> class and sets up event handlers.
        /// </summary>
        public Form1()
        {
            InitializeComponent();
            listBoxDevices.SelectedIndexChanged += ListBoxDevices_SelectedIndexChanged;
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
            var address = ulong.Parse(selected.Split('[')[1].TrimEnd(']'));
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

            var services = await device.GetGattServicesAsync();
            foreach (var service in services.Services)
            {
                var chars = await service.GetCharacteristicsAsync();
                foreach (var c in chars.Characteristics)
                {
                    if (c.Uuid.ToString().ToUpper() == PMD_CONTROL)
                    {
                        await c.ReadValueAsync();
                        await c.WriteValueAsync(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(ECG_WRITE));
                    }
                    if (c.Uuid.ToString().ToUpper() == PMD_DATA)
                    {
                        ecgChar = c;
                        ecgChar.ValueChanged += EcgChar_ValueChanged;
                        await ecgChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    }
                }
            }

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

                outlet.push_sample(new float[] { raw });
                offset += step;
            }
            // Animate "Streaming" label with dots to show activity
            dotState = (dotState + 1) % 4;
            string dots = new string('.', dotState);
            BeginInvoke(() => lblStatus.Text = $"Streaming{dots}");
        }

        /// <summary>
        /// Initializes and starts the LSL (LabStreamingLayer) outlet stream for ECG data.
        /// </summary>
        /// <param name="name">The name of the device used as the stream name.</param>
        /// <param name="id">A unique identifier for the stream based on the Bluetooth address.</param>
        private void StartLSL(string name, string id)
        {
            var info = new StreamInfo(name, "ECG", 1, 130, channel_format_t.cf_float32, id);
            var channels = info.desc().append_child("channels");
            channels.append_child("channel")
                .append_child_value("name", "ECG")
                .append_child_value("unit", "microvolts")
                .append_child_value("type", "ECG");

            outlet = new StreamOutlet(info, 74, 360);
        }
    }
}
