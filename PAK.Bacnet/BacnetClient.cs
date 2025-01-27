using System.Net;
using System.Net.Sockets;

namespace PAK.Bacnet
{
    public class BacnetClient : IDisposable
    {
        private readonly string _ipAddress;
        private readonly int _port;
        private readonly int _timeout;
        private readonly UdpClient _udpClient;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _isConnected;
        private readonly Dictionary<uint, CovSubscription> _subscriptions = new();
        private readonly object _lock = new();

        public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;
        public event EventHandler<CovNotificationEventArgs>? CovNotificationReceived;

        public BacnetClient(string ipAddress, int port = 47808, int timeout = 8000)
        {
            _ipAddress = ipAddress;
            _port = port;
            _timeout = timeout;
            _udpClient = new UdpClient();
        }

        public async Task ConnectAsync()
        {
            try
            {
                _udpClient.Client.ReceiveTimeout = _timeout;
                _udpClient.Client.SendTimeout = _timeout;
                
                // BACnet typically uses UDP broadcast
                _udpClient.EnableBroadcast = true;
                
                // Bind to any available port
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                
                _isConnected = true;
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(true, null));

                // Start listening for responses
                _ = ListenForResponsesAsync();
                
                // Start connection monitoring
                _ = MonitorConnectionAsync();

                // Send Who-Is broadcast to discover devices
                await SendWhoIsAsync();
            }
            catch (Exception ex)
            {
                _isConnected = false;
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, ex.Message));
                throw new BacnetException($"Failed to initialize BACnet client: {ex.Message}", ex);
            }
        }

        private async Task ListenForResponsesAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(_cancellationTokenSource.Token);
                    ProcessResponse(result.Buffer);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isConnected)
                    {
                        _isConnected = false;
                        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, ex.Message));
                    }
                }
            }
        }

        private void ProcessResponse(byte[] data)
        {
            // Basic BACnet Virtual Link Layer (BVLL) header processing
            if (data.Length < 4) return;

            var bvlcType = data[0];
            var bvlcFunction = data[1];
            var bvlcLength = (data[2] << 8) | data[3];

            if (bvlcType != 0x81) return; // Not BACnet/IP
            
            switch (bvlcFunction)
            {
                case 0x00: // BVLC-Result
                    ProcessBvlcResult(data);
                    break;
                case 0x01: // Write-Broadcast-Distribution-Table
                    break;
                case 0x02: // Read-Broadcast-Distribution-Table
                    break;
                case 0x03: // Read-Broadcast-Distribution-Table-Ack
                    break;
                case 0x04: // Forwarded-NPDU
                    ProcessNpdu(data, 4);
                    break;
                case 0x05: // Register-Foreign-Device
                    break;
                case 0x06: // Read-Foreign-Device-Table
                    break;
                case 0x07: // Read-Foreign-Device-Table-Ack
                    break;
                case 0x08: // Delete-Foreign-Device-Table-Entry
                    break;
                case 0x09: // Distribute-Broadcast-To-Network
                    break;
                case 0x0A: // Original-Unicast-NPDU
                    ProcessNpdu(data, 4);
                    break;
                case 0x0B: // Original-Broadcast-NPDU
                    ProcessNpdu(data, 4);
                    break;
            }
        }

        private void ProcessBvlcResult(byte[] data)
        {
            if (data.Length < 6) return;
            var result = (data[4] << 8) | data[5];
            // Process BVLC result code
        }

        private void ProcessNpdu(byte[] data, int offset)
        {
            if (data.Length < offset + 2) return;

            var version = data[offset];
            var control = data[offset + 1];

            if (version != 0x01) return; // Not BACnet protocol version 1

            // Process NPDU control byte and handle message accordingly
            // This is where you would process COV notifications, responses, etc.
        }

        private async Task MonitorConnectionAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10000, _cancellationTokenSource.Token); // Check every 10 seconds
                    await SendWhoIsAsync(); // Keep-alive by sending Who-Is
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    if (_isConnected)
                    {
                        _isConnected = false;
                        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, "Connection lost"));
                    }
                }
            }
        }

        public async Task SendWhoIsAsync()
        {
            var whoIsMessage = new byte[]
            {
                0x81, // BVLL Type (BACnet/IP)
                0x0B, // Original-Broadcast-NPDU
                0x00, 0x0C, // BVLL Length
                0x01, // Version
                0x20, // NPDU control (no additional info)
                0x10, // APDU Type (Unconfirmed-Request)
                0x08, // Service Choice (Who-Is)
                0x00, 0x00, // Low limit
                0xFF, 0xFF  // High limit
            };

            await SendMessageAsync(whoIsMessage);
        }

        public async Task SubscribeCovAsync(uint objectId, uint propertyId)
        {
            // Note: This is a simplified COV subscription message
            var covSubscribeMessage = new byte[]
            {
                0x81, // BVLL Type (BACnet/IP)
                0x0A, // Original-Unicast-NPDU
                0x00, 0x11, // BVLL Length
                0x01, // Version
                0x04, // NPDU control (expecting reply)
                0x00, // APDU Type (Confirmed-Request)
                0x05, // Service Choice (Subscribe-COV)
                // Object ID and property details would follow...
            };

            await SendMessageAsync(covSubscribeMessage);

            lock (_lock)
            {
                _subscriptions[objectId] = new CovSubscription
                {
                    ObjectId = objectId,
                    PropertyId = propertyId,
                    SubscriptionTime = DateTime.UtcNow
                };
            }
        }

        private async Task SendMessageAsync(byte[] message)
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(_ipAddress), _port);
                await _udpClient.SendAsync(message, message.Length, endpoint);
            }
            catch (Exception ex)
            {
                throw new BacnetException("Failed to send message", ex);
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _udpClient.Dispose();
            _cancellationTokenSource.Dispose();
        }
    }

    public class BacnetException : Exception
    {
        public BacnetException(string message) : base(message) { }
        public BacnetException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ConnectionStatusEventArgs : EventArgs
    {
        public bool IsConnected { get; }
        public string? ErrorMessage { get; }

        public ConnectionStatusEventArgs(bool isConnected, string? errorMessage)
        {
            IsConnected = isConnected;
            ErrorMessage = errorMessage;
        }
    }

    public class CovNotificationEventArgs : EventArgs
    {
        public uint ObjectId { get; }
        public uint PropertyId { get; }
        public object? Value { get; }

        public CovNotificationEventArgs(uint objectId, uint propertyId, object? value)
        {
            ObjectId = objectId;
            PropertyId = propertyId;
            Value = value;
        }
    }

    internal class CovSubscription
    {
        public uint ObjectId { get; set; }
        public uint PropertyId { get; set; }
        public DateTime SubscriptionTime { get; set; }
    }
}
