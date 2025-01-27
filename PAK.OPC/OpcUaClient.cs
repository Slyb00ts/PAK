using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System.Collections.Concurrent;

namespace PAK.OPC
{
    public class OpcUaClient : IDisposable
    {
        private ISession? _session;
        private SessionReconnectHandler? _reconnectHandler;
        private readonly ApplicationConfiguration _config;
        private readonly ConcurrentDictionary<uint, Subscription> _subscriptions;
        private const int ReconnectPeriod = 10;

        public event EventHandler<DataChangeEventArgs>? DataChanged;
        public event EventHandler<ServiceResult>? ConnectionStatusChanged;

        public OpcUaClient()
        {
            _subscriptions = new ConcurrentDictionary<uint, Subscription>();
            _config = CreateApplicationConfiguration();
        }

        private ApplicationConfiguration CreateApplicationConfiguration()
        {
            var config = new ApplicationConfiguration
            {
                ApplicationName = "PAK.OPC.Client",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier(),
                    TrustedPeerCertificates = new CertificateTrustList(),
                    TrustedIssuerCertificates = new CertificateTrustList(),
                    RejectedCertificateStore = new CertificateTrustList(),
                    AutoAcceptUntrustedCertificates = true,
                    RejectSHA1SignedCertificates = false,
                    MinimumCertificateKeySize = 1024
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas 
                { 
                    OperationTimeout = 60000,
                    MaxStringLength = 1048576,
                    MaxByteStringLength = 1048576,
                    MaxArrayLength = 65535,
                    MaxMessageSize = 4194304,
                    MaxBufferSize = 65535,
                    ChannelLifetime = 300000,
                    SecurityTokenLifetime = 3600000
                },
                ClientConfiguration = new ClientConfiguration 
                { 
                    DefaultSessionTimeout = 60000,
                    MinSubscriptionLifetime = 10000
                },
                DisableHiResClock = true
            };

            config.Validate(ApplicationType.Client).GetAwaiter().GetResult();

            if (config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
            {
                config.CertificateValidator.CertificateValidation += (s, e) => 
                { 
                    e.Accept = true; 
                };
            }

            return config;
        }

        public async Task ConnectAsync(string serverUrl)
        {
            try
            {
                // Discover endpoints
                var endpoints = await DiscoverEndpointsAsync(serverUrl);
                if (endpoints == null || endpoints.Count == 0)
                {
                    throw new Exception($"No endpoints found at {serverUrl}");
                }

                // Select endpoint - prefer no security for testing
                var selectedEndpoint = endpoints.FirstOrDefault(e => e.SecurityMode == MessageSecurityMode.None) 
                    ?? endpoints.FirstOrDefault();

                if (selectedEndpoint == null)
                {
                    throw new Exception("No suitable endpoint found");
                }

                var endpointConfiguration = EndpointConfiguration.Create(_config);
                var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);

                // Create and activate session
                _session = await Session.Create(
                    _config,
                    endpoint,
                    false,
                    _config.ApplicationName,
                    60000,
                    new UserIdentity(new AnonymousIdentityToken()),
                    null).ConfigureAwait(false);

                _session.KeepAlive += Session_KeepAlive!;
                
                // Notify initial connection success
                ConnectionStatusChanged?.Invoke(this, StatusCodes.Good);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to connect to {serverUrl}: {ex.Message}", ex);
            }
        }

        private async Task<EndpointDescriptionCollection> DiscoverEndpointsAsync(string serverUrl)
        {
            // Try with discovery first
            try
            {
                using (var discoveryClient = DiscoveryClient.Create(new Uri(serverUrl)))
                {
                    return await Task.FromResult(discoveryClient.GetEndpoints(null));
                }
            }
            catch
            {
                // If discovery fails, try creating a dummy endpoint for direct connection
                var uri = new Uri(serverUrl);
                var endpointDescription = new EndpointDescription
                {
                    EndpointUrl = serverUrl,
                    Server = new ApplicationDescription
                    {
                        ApplicationUri = $"urn:{uri.DnsSafeHost}:UAServer",
                        ApplicationType = ApplicationType.Server,
                    },
                    SecurityMode = MessageSecurityMode.None,
                    SecurityPolicyUri = SecurityPolicies.None,
                    UserIdentityTokens = new UserTokenPolicyCollection
                    {
                        new UserTokenPolicy
                        {
                            TokenType = UserTokenType.Anonymous,
                            SecurityPolicyUri = SecurityPolicies.None
                        }
                    },
                    TransportProfileUri = Profiles.UaTcpTransport
                };

                return new EndpointDescriptionCollection { endpointDescription };
            }
        }

        private void Session_KeepAlive(ISession session, KeepAliveEventArgs e)
        {
            if (e.Status != null)
            {
                ConnectionStatusChanged?.Invoke(this, e.Status);
                
                if (ServiceResult.IsNotGood(e.Status))
                {
                    if (_reconnectHandler == null)
                    {
                        _reconnectHandler = new SessionReconnectHandler();
                        _reconnectHandler.BeginReconnect(session, ReconnectPeriod * 1000, Server_ReconnectComplete);
                    }
                }
            }
        }

        private void Server_ReconnectComplete(object? sender, EventArgs e)
        {
            if (_reconnectHandler != null)
            {
                _session = _reconnectHandler.Session;
                _reconnectHandler.Dispose();
                _reconnectHandler = null;
                
                // Notify successful reconnection
                ConnectionStatusChanged?.Invoke(this, StatusCodes.Good);
            }
        }

        public async Task<ReferenceDescriptionCollection> BrowseNodeAsync(NodeId nodeId)
        {
            ThrowIfNotConnected();

            var browser = new Browser(_session)
            {
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (int)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (int)BrowseResultMask.All
            };

            return browser.Browse(nodeId);
        }

        public async Task<DataValue> ReadNodeAsync(NodeId nodeId)
        {
            ThrowIfNotConnected();
            return await _session.ReadValueAsync(nodeId);
        }

        public Subscription CreateSubscription(int publishingInterval = 1000)
        {
            ThrowIfNotConnected();

            var subscription = new Subscription(_session.DefaultSubscription)
            {
                PublishingInterval = publishingInterval
            };

            _session.AddSubscription(subscription);
            subscription.Create();
            _subscriptions.TryAdd(subscription.Id, subscription);

            return subscription;
        }

        public void AddMonitoredItem(Subscription subscription, NodeId nodeId)
        {
            var item = new MonitoredItem(subscription.DefaultItem)
            {
                DisplayName = nodeId.ToString(),
                StartNodeId = nodeId
            };

            item.Notification += OnMonitoredItemNotification;
            subscription.AddItem(item);
            subscription.ApplyChanges();
        }

        private void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
        {
            if (e.NotificationValue is MonitoredItemNotification notification)
            {
                DataChanged?.Invoke(this, new DataChangeEventArgs
                {
                    NodeId = item.StartNodeId,
                    Value = notification.Value
                });
            }
        }

        private void ThrowIfNotConnected()
        {
            if (_session == null || !_session.Connected)
                throw new InvalidOperationException("Client is not connected to server");
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Delete(true);
            }
            _subscriptions.Clear();

            if (_session is Session session)
            {
                session.Close();
                session.Dispose();
            }
            
            _reconnectHandler?.Dispose();
        }
    }

    public class DataChangeEventArgs : EventArgs
    {
        public NodeId? NodeId { get; set; }
        public DataValue? Value { get; set; }
    }
}
