using System.Net.Sockets;
using NModbus;
using NModbus.Device;

namespace PAK.Modbus
{
    public class ModbusClient : IDisposable
    {
        private readonly string _ipAddress;
        private readonly int _port;
        private readonly int _timeout;
        private TcpClient? _tcpClient;
        private IModbusMaster? _master;
        private readonly object _lock = new object();
        private bool _isConnected;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;

        public ModbusClient(string ipAddress, int port = 502, int timeout = 8000)
        {
            _ipAddress = ipAddress;
            _port = port;
            _timeout = timeout;
        }

        public async Task ConnectAsync()
        {
            try
            {
                _tcpClient = new TcpClient();
                using var timeoutCts = new CancellationTokenSource(_timeout);
                
                await _tcpClient.ConnectAsync(_ipAddress, _port).WaitAsync(timeoutCts.Token);
                
                _tcpClient.ReceiveTimeout = _timeout;
                _tcpClient.SendTimeout = _timeout;

                var factory = new ModbusFactory();
                _master = factory.CreateMaster(_tcpClient);

                _isConnected = true;
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(true, null));

                // Start connection monitoring
                _ = MonitorConnectionAsync();
            }
            catch (Exception ex)
            {
                _isConnected = false;
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, ex.Message));
                throw new ModbusException($"Failed to connect to {_ipAddress}:{_port}", ex);
            }
        }

        private async Task MonitorConnectionAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, _cancellationTokenSource.Token);

                    if (_tcpClient?.Client == null || !_tcpClient.Connected)
                    {
                        if (_isConnected)
                        {
                            _isConnected = false;
                            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, "Connection lost"));
                        }
                        await ReconnectAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Ignore other exceptions in monitoring task
                }
            }
        }

        private async Task ReconnectAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested && !_isConnected)
            {
                try
                {
                    await ConnectAsync();
                    break;
                }
                catch
                {
                    await Task.Delay(5000, _cancellationTokenSource.Token);
                }
            }
        }

        public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints)
        {
            try
            {
                ThrowIfNotConnected();
                using var cts = new CancellationTokenSource(_timeout);
                
                return await Task.Run(() =>
                {
                    lock (_lock)
                    {
                        return _master!.ReadHoldingRegisters(1, startAddress, numberOfPoints);
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                throw new ModbusException($"Failed to read holding registers at address {startAddress}", ex);
            }
        }

        public async Task<bool[]> ReadCoilsAsync(ushort startAddress, ushort numberOfPoints)
        {
            try
            {
                ThrowIfNotConnected();
                using var cts = new CancellationTokenSource(_timeout);

                return await Task.Run(() =>
                {
                    lock (_lock)
                    {
                        return _master!.ReadCoils(1, startAddress, numberOfPoints);
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                throw new ModbusException($"Failed to read coils at address {startAddress}", ex);
            }
        }

        public async Task WriteHoldingRegisterAsync(ushort registerAddress, ushort value)
        {
            try
            {
                ThrowIfNotConnected();
                using var cts = new CancellationTokenSource(_timeout);

                await Task.Run(() =>
                {
                    lock (_lock)
                    {
                        _master!.WriteSingleRegister(1, registerAddress, value);
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                throw new ModbusException($"Failed to write holding register at address {registerAddress}", ex);
            }
        }

        public async Task WriteCoilAsync(ushort coilAddress, bool value)
        {
            try
            {
                ThrowIfNotConnected();
                using var cts = new CancellationTokenSource(_timeout);

                await Task.Run(() =>
                {
                    lock (_lock)
                    {
                        _master!.WriteSingleCoil(1, coilAddress, value);
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                throw new ModbusException($"Failed to write coil at address {coilAddress}", ex);
            }
        }

        private void ThrowIfNotConnected()
        {
            if (!_isConnected || _master == null)
                throw new ModbusException("Client is not connected");
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _master?.Dispose();
            _tcpClient?.Dispose();
            _cancellationTokenSource.Dispose();
        }
    }

    public class ModbusException : Exception
    {
        public ModbusException(string message) : base(message) { }
        public ModbusException(string message, Exception innerException) : base(message, innerException) { }
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
}
