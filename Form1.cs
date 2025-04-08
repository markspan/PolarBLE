using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using LSL;

namespace PolarBLE
{
    public partial class Form1 : Form
    {
        BluetoothLEAdvertisementWatcher watcher;
        Dictionary<ulong, BluetoothLEAdvertisementReceivedEventArgs> devices = new();
        StreamOutlet outlet;
        GattCharacteristic ecgChar;
        private const string PMD_CONTROL = "FB005C81-02E7-F387-1CAD-8ACD2D8DF0C8";
        private const string PMD_DATA = "FB005C82-02E7-F387-1CAD-8ACD2D8DF0C8";
        private static readonly byte[] ECG_WRITE = new byte[]
        {
        0x02, 0x00, 0x00, 0x01, 0x82, 0x00, 0x01, 0x01, 0x0E, 0x00
        };

        public Form1()
        {
            InitializeComponent();
            listBoxDevices.SelectedIndexChanged += ListBoxDevices_SelectedIndexChanged;
        }

        private void btnScan_Click(object sender, EventArgs e)
        {
            devices.Clear();
            listBoxDevices.Items.Clear();

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

        private async void ListBoxDevices_SelectedIndexChanged(object sender, EventArgs e)
        {
            watcher?.Stop();
            lblStatus.Text = "Connecting...";

            var selected = listBoxDevices.SelectedItem.ToString();
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
            lblStatus.Text = "Streaming...";
        }

        private unsafe void EcgChar_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
            byte[] data = new byte[args.CharacteristicValue.Length];
            reader.ReadBytes(data);

            if (data[0] != 0x00) return;

            int step = 3;
            int offset = 10;
            while (offset + step <= data.Length)
            {
                int ecg = BitConverter.ToInt32(new byte[] { data[offset], data[offset + 1], data[offset + 2], 0x00 }, 0);
                outlet.push_sample(new float[] { ecg });
                offset += step;
            }
        }

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
