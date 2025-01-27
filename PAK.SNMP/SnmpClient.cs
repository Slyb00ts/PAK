using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace PAK.SNMP
{
    public class SnmpClient : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _timeout;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _isTrapListenerActive;
        private Socket? _trapSocket;

        public event EventHandler<SnmpTrapEventArgs>? TrapReceived;
        public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;

        public SnmpClient(string host, int port = 161, int timeout = 8000)
        {
            _host = host;
            _port = port;
            _timeout = timeout;
        }

        public async Task<IList<SnmpVariable>> GetAsync(string oid, SnmpVersion version, string community = "public")
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(_host), _port);
                var variables = new List<Variable> { new Variable(new ObjectIdentifier(oid)) };
                
                var result = await Task.Run(() => Messenger.Get(ToVersionCode(version),
                    endpoint,
                    new OctetString(community),
                    variables,
                    _timeout));
                
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(true, null));
                return result.Select(v => new SnmpVariable(v)).ToList();
            }
            catch (Exception ex)
            {
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, ex.Message));
                throw new SnmpException($"Failed to get OID {oid}", ex);
            }
        }

        public async Task<IList<SnmpVariable>> GetV3Async(string oid, string username, string authPhrase, string privPhrase, 
            AuthenticationMethod auth = AuthenticationMethod.SHA1,
            PrivacyMethod priv = PrivacyMethod.AES128)
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(_host), _port);
                var privacy = GetPrivacyProvider(priv, authPhrase, privPhrase);
                var auth_provider = GetAuthenticationProvider(auth, authPhrase);
                var variables = new List<Variable> { new Variable(new ObjectIdentifier(oid)) };

                var request = new GetRequestMessage(
                    Messenger.NextRequestId,
                    VersionCode.V3,
                    new OctetString(username),
                    variables);

                var response = await Task.Run(() => request.GetResponse(_timeout, endpoint));
                var result = response.Pdu().Variables;
                
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(true, null));
                return result.Select(v => new SnmpVariable(v)).ToList();
            }
            catch (Exception ex)
            {
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, ex.Message));
                throw new SnmpException($"Failed to get OID {oid}", ex);
            }
        }

        public async Task WalkAsync(string oid, Action<SnmpVariable> callback, SnmpVersion version, string community = "public")
        {
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(_host), _port);
                var rootOid = new ObjectIdentifier(oid);
                var currentOid = rootOid;
                
                await Task.Run(() =>
                {
                    while (true)
                    {
                        var variables = new List<Variable> { new Variable(currentOid) };
                        var request = new GetNextRequestMessage(
                            Messenger.NextRequestId,
                            ToVersionCode(version),
                            new OctetString(community),
                            variables);

                        var response = request.GetResponse(_timeout, endpoint);
                        if (response.Pdu().Variables.Count == 0) break;

                        var variable = response.Pdu().Variables[0];
                        if (variable == null || !variable.Id.ToString().StartsWith(rootOid.ToString())) break;

                        callback(new SnmpVariable(variable));
                        currentOid = variable.Id;
                    }
                });
                
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(true, null));
            }
            catch (Exception ex)
            {
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, ex.Message));
                throw new SnmpException($"Failed to walk OID {oid}", ex);
            }
        }

        public void StartTrapListener(int port = 162)
        {
            if (_isTrapListenerActive) return;

            try
            {
                _trapSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _trapSocket.Bind(new IPEndPoint(IPAddress.Any, port));
                _isTrapListenerActive = true;

                // Start listening for traps
                _ = ListenForTrapsAsync();
            }
            catch (Exception ex)
            {
                throw new SnmpException("Failed to start TRAP listener", ex);
            }
        }

        private async Task ListenForTrapsAsync()
        {
            var buffer = new byte[64 * 1024];
            var endpoint = new IPEndPoint(IPAddress.Any, 0);

            while (!_cancellationTokenSource.Token.IsCancellationRequested && _isTrapListenerActive)
            {
                try
                {
                    var result = await Task.Run(() =>
                    {
                        EndPoint remoteEndpoint = endpoint;
                        return _trapSocket!.ReceiveFrom(buffer, ref remoteEndpoint);
                    });

                    if (result > 0)
                    {
                        var data = new byte[result];
                        Buffer.BlockCopy(buffer, 0, data, 0, result);
                        
                        var messages = MessageFactory.ParseMessages(data, new UserRegistry());
                        foreach (var message in messages)
                        {
                            if (message.Pdu() is TrapV1Pdu v1Trap)
                            {
                                TrapReceived?.Invoke(this, new SnmpTrapEventArgs(
                                    v1Trap.Enterprise.ToString(),
                                    v1Trap.AgentAddress.ToString(),
                                    (int)v1Trap.Generic,
                                    v1Trap.Specific,
                                    (uint)v1Trap.TimeStamp.ToUInt32(),
                                    v1Trap.Variables.ToList()
                                ));
                            }
                            else if (message.Pdu() is TrapV2Pdu v2Trap)
                            {
                                TrapReceived?.Invoke(this, new SnmpTrapEventArgs(
                                    "v2Trap",
                                    endpoint.Address.ToString(),
                                    -1,
                                    -1,
                                    0,
                                    v2Trap.Variables.ToList()
                                ));
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log trap processing error but continue listening
                    Console.WriteLine($"Error processing TRAP: {ex.Message}");
                }
            }
        }

        public void StopTrapListener()
        {
            _isTrapListenerActive = false;
            _trapSocket?.Close();
            _trapSocket = null;
        }

        private IPrivacyProvider GetPrivacyProvider(PrivacyMethod method, string authPhrase, string privPhrase)
        {
            var auth = GetAuthenticationProvider(AuthenticationMethod.SHA1, authPhrase);
            return method switch
            {
                PrivacyMethod.DES => new DESPrivacyProvider(new OctetString(privPhrase), auth),
                PrivacyMethod.AES128 => new AESPrivacyProvider(new OctetString(privPhrase), auth),
                PrivacyMethod.AES192 => new AES192PrivacyProvider(new OctetString(privPhrase), auth),
                PrivacyMethod.AES256 => new AES256PrivacyProvider(new OctetString(privPhrase), auth),
                _ => throw new ArgumentException("Unsupported privacy method")
            };
        }

        private IAuthenticationProvider GetAuthenticationProvider(AuthenticationMethod method, string phrase)
        {
            return method switch
            {
                AuthenticationMethod.MD5 => new MD5AuthenticationProvider(new OctetString(phrase)),
                AuthenticationMethod.SHA1 => new SHA1AuthenticationProvider(new OctetString(phrase)),
                AuthenticationMethod.SHA256 => new SHA256AuthenticationProvider(new OctetString(phrase)),
                AuthenticationMethod.SHA384 => new SHA384AuthenticationProvider(new OctetString(phrase)),
                AuthenticationMethod.SHA512 => new SHA512AuthenticationProvider(new OctetString(phrase)),
                _ => throw new ArgumentException("Unsupported authentication method")
            };
        }

        private VersionCode ToVersionCode(SnmpVersion version)
        {
            return version switch
            {
                SnmpVersion.V1 => VersionCode.V1,
                SnmpVersion.V2 => VersionCode.V2,
                SnmpVersion.V3 => VersionCode.V3,
                _ => throw new ArgumentException("Invalid SNMP version")
            };
        }

        public void LoadMibFile(string filePath)
        {
            try
            {
                // Note: SharpSnmpLib doesn't include direct MIB parsing
                // For full MIB support, we'd need to implement a custom MIB parser
                // or use additional libraries
                throw new NotImplementedException("MIB file parsing is not implemented yet");
            }
            catch (Exception ex)
            {
                throw new SnmpException("Failed to load MIB file", ex);
            }
        }

        public void Dispose()
        {
            StopTrapListener();
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _trapSocket?.Dispose();
        }
    }

    public class SnmpException : Exception
    {
        public SnmpException(string message) : base(message) { }
        public SnmpException(string message, Exception innerException) : base(message, innerException) { }
    }
}
