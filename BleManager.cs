using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace beematrix_doom
{
    public class BleManager
    {
        private static readonly Guid ServiceUuid = Guid.Parse("0000fa00-0000-1000-8000-00805f9b34fb"); // Typical iDotMatrix service UUID prefix (FA00)
        private static readonly Guid WriteCharUuid = Guid.Parse("0000fa02-0000-1000-8000-00805f9b34fb");
        
        public static BleManager Instance { get; } = new BleManager();

        private BluetoothLEDevice? _device;
        private GattCharacteristic? _writeCharacteristic;
        
        public bool IsConnected => _device != null && _device.ConnectionStatus == BluetoothConnectionStatus.Connected && _writeCharacteristic != null;
        public string? ConnectedDeviceName => _device?.Name;

        public event Action<string>? LogMessage;
        public event Action? ConnectionStateChanged;

        private BleManager() { }

        private void Log(string msg) => LogMessage?.Invoke(msg);

        public async Task<bool> ConnectByAddressAsync(string macAddress)
        {
            try
            {
                string cleanAddress = macAddress.Replace(":", "").Replace("-", "").Trim();
                ulong address = Convert.ToUInt64(cleanAddress, 16);
                return await ConnectByAddressAsync(address);
            }
            catch (Exception ex)
            {
                Log($"Failed to parse MAC address: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ConnectByAddressAsync(ulong bluetoothAddress)
        {
            try
            {
                Log($"Connecting to device at address {bluetoothAddress:X12}...");
                Disconnect();

                // Start an active advertisement watcher to force Windows to populate the advertisement cache
                Log("Starting active BLE advertisement watcher to discover device...");
                var tcs = new TaskCompletionSource<bool>();
                var watcher = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher();
                watcher.ScanningMode = Windows.Devices.Bluetooth.Advertisement.BluetoothLEScanningMode.Active;

                Windows.Foundation.TypedEventHandler<Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher, Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementReceivedEventArgs> receivedHandler = (w, args) =>
                {
                    if (args.BluetoothAddress == bluetoothAddress)
                    {
                        Log($"Detected advertisement from target device {bluetoothAddress:X12}!");
                        tcs.TrySetResult(true);
                    }
                };

                watcher.Received += receivedHandler;
                watcher.Start();

                // Wait up to 5 seconds for advertisement discovery
                var delayTask = Task.Delay(TimeSpan.FromSeconds(5));
                var completedTask = await Task.WhenAny(tcs.Task, delayTask);

                watcher.Stop();
                watcher.Received -= receivedHandler;

                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
                if (_device == null)
                {
                    Log("Failed to open Bluetooth device by address.");
                    return false;
                }

                _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;

                Log("Discovering services...");
                var servicesResult = await _device.GetGattServicesForUuidAsync(ServiceUuid);
                if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                {
                    // Fallback query all services to find character
                    var allServices = await _device.GetGattServicesAsync();
                    if (allServices.Status == GattCommunicationStatus.Success)
                    {
                        foreach (var service in allServices.Services)
                        {
                            var charsResult = await service.GetCharacteristicsForUuidAsync(WriteCharUuid);
                            if (charsResult.Status == GattCommunicationStatus.Success && charsResult.Characteristics.Count > 0)
                            {
                                _writeCharacteristic = charsResult.Characteristics[0];
                                Log($"Found write characteristic inside service: {service.Uuid}");
                                break;
                            }
                        }
                    }
                }
                else
                {
                    var service = servicesResult.Services[0];
                    var charsResult = await service.GetCharacteristicsForUuidAsync(WriteCharUuid);
                    if (charsResult.Status == GattCommunicationStatus.Success && charsResult.Characteristics.Count > 0)
                    {
                        _writeCharacteristic = charsResult.Characteristics[0];
                    }
                }

                if (_writeCharacteristic == null)
                {
                    Log("Write characteristic FA02 not found on device.");
                    Disconnect();
                    return false;
                }

                Log("Device connected successfully!");
                ConnectionStateChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Log($"Connection by address failed: {ex.Message}");
                Disconnect();
                return false;
            }
        }

        private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            Log($"Device connection status changed: {sender.ConnectionStatus}");
            ConnectionStateChanged?.Invoke();
        }

        public void Disconnect()
        {
            if (_device != null)
            {
                _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
                _device.Dispose();
                _device = null;
            }
            _writeCharacteristic = null;
            Log("Disconnected from BLE device.");
            ConnectionStateChanged?.Invoke();
        }

        public async Task<bool> SendModeCommandAsync(byte mode)
        {
            if (!IsConnected || _writeCharacteristic == null) return false;
            byte[] cmd = new byte[] { 5, 0, 4, 1, mode };
            return await WriteRawBytesAsync(cmd);
        }

        public async Task<bool> SendImagePayloadAsync(byte[] pngData)
        {
            if (!IsConnected || _writeCharacteristic == null) return false;

            try
            {
                int chunkCount = (int)Math.Ceiling(pngData.Length / 4096.0);
                short totalSizeIdx = (short)(pngData.Length + chunkCount);
                byte[] sizeIdxBytes = BitConverter.GetBytes(totalSizeIdx);
                byte[] fileLenBytes = BitConverter.GetBytes(pngData.Length);

                using (MemoryStream ms = new MemoryStream())
                {
                    for (int i = 0; i < chunkCount; i++)
                    {
                        int offset = i * 4096;
                        int size = Math.Min(4096, pngData.Length - offset);
                        
                        ms.Write(sizeIdxBytes, 0, 2);
                        ms.Write(new byte[] { 0, 0 }, 0, 2);
                        ms.WriteByte((byte)(i > 0 ? 2 : 0));
                        ms.Write(fileLenBytes, 0, 4);
                        ms.Write(pngData, offset, size);
                    }

                    byte[] fullPayload = ms.ToArray();
                    return await WriteRawBytesAsync(fullPayload);
                }
            }
            catch (Exception ex)
            {
                Log($"Error building image payload: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> WriteRawBytesAsync(byte[] data)
        {
            if (!IsConnected || _writeCharacteristic == null) return false;

            try
            {
                int maxWriteSize = 512;

                for (int i = 0; i < data.Length; i += maxWriteSize)
                {
                    int size = Math.Min(maxWriteSize, data.Length - i);
                    byte[] chunk = new byte[size];
                    Buffer.BlockCopy(data, i, chunk, 0, size);

                    var writeResult = await _writeCharacteristic.WriteValueWithResultAsync(
                        chunk.AsBuffer(), 
                        GattWriteOption.WriteWithoutResponse
                    );

                    if (writeResult.Status != GattCommunicationStatus.Success)
                    {
                        return false;
                    }
                }

                await Task.Delay(5); // Small cooldown
                return true;
            }
            catch (Exception ex)
            {
                Log($"Error sending GATT data: {ex.Message}");
                return false;
            }
        }
    }
}
