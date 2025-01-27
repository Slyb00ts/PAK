using RestSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PAK.OpenAPI
{
    public class OpenApiClient : IDisposable
    {
        private readonly RestClient _client;
        private readonly string _baseUrl;
        private readonly Dictionary<string, OpenApiEndpoint> _endpoints;
        private readonly Dictionary<string, string> _globalHeaders;
        private string? _bearerToken;

        public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;

        public OpenApiClient(string baseUrl, int timeout = 8000)
        {
            _baseUrl = baseUrl;
            _client = new RestClient(new RestClientOptions
            {
                BaseUrl = new Uri(baseUrl),
                ThrowOnAnyError = false,
                MaxTimeout = timeout
            });

            _endpoints = new Dictionary<string, OpenApiEndpoint>();
            _globalHeaders = new Dictionary<string, string>();
        }

        public async Task LoadSpecificationFromUrlAsync(string specUrl)
        {
            try
            {
                var request = new RestRequest(specUrl);
                var response = await _client.ExecuteGetAsync(request);

                if (!response.IsSuccessful)
                {
                    throw new OpenApiException($"Failed to load OpenAPI specification: {response.ErrorMessage}");
                }

                if (specUrl.EndsWith(".yaml") || specUrl.EndsWith(".yml"))
                {
                    LoadYamlSpecification(response.Content!);
                }
                else
                {
                    LoadJsonSpecification(response.Content!);
                }

                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(true, null));
            }
            catch (Exception ex)
            {
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, ex.Message));
                throw new OpenApiException("Failed to load OpenAPI specification", ex);
            }
        }

        public void LoadSpecificationFromFile(string filePath)
        {
            try
            {
                var content = File.ReadAllText(filePath);

                if (filePath.EndsWith(".yaml") || filePath.EndsWith(".yml"))
                {
                    LoadYamlSpecification(content);
                }
                else
                {
                    LoadJsonSpecification(content);
                }

                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(true, null));
            }
            catch (Exception ex)
            {
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, ex.Message));
                throw new OpenApiException("Failed to load OpenAPI specification", ex);
            }
        }

        private void LoadJsonSpecification(string content)
        {
            var spec = JsonConvert.DeserializeObject<JObject>(content);
            if (spec == null)
            {
                throw new OpenApiException("Invalid OpenAPI specification format");
            }

            ParseSpecification(spec);
        }

        private void LoadYamlSpecification(string content)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            var yamlObject = deserializer.Deserialize<Dictionary<string, object>>(content);
            var jsonContent = JsonConvert.SerializeObject(yamlObject);
            var spec = JsonConvert.DeserializeObject<JObject>(jsonContent);

            if (spec == null)
            {
                throw new OpenApiException("Invalid OpenAPI specification format");
            }

            ParseSpecification(spec);
        }

        private void ParseSpecification(JObject spec)
        {
            _endpoints.Clear();

            var paths = spec["paths"] as JObject;
            if (paths == null) return;

            foreach (var path in paths.Properties())
            {
                var methods = path.Value as JObject;
                if (methods == null) continue;

                foreach (var method in methods.Properties())
                {
                    var operation = method.Value as JObject;
                    if (operation == null) continue;

                    var endpoint = new OpenApiEndpoint
                    {
                        Path = path.Name,
                        Method = method.Name.ToUpper(),
                        OperationId = operation["operationId"]?.ToString(),
                        Parameters = ParseParameters(operation["parameters"] as JArray),
                        RequestBody = ParseRequestBody(operation["requestBody"] as JObject),
                        Responses = ParseResponses(operation["responses"] as JObject)
                    };

                    if (!string.IsNullOrEmpty(endpoint.OperationId))
                    {
                        _endpoints[endpoint.OperationId] = endpoint;
                    }
                }
            }
        }

        private List<OpenApiParameter> ParseParameters(JArray? parameters)
        {
            var result = new List<OpenApiParameter>();
            if (parameters == null) return result;

            foreach (var param in parameters)
            {
                result.Add(new OpenApiParameter
                {
                    Name = param["name"]?.ToString(),
                    In = param["in"]?.ToString(),
                    Required = param["required"]?.Value<bool>() ?? false,
                    Schema = param["schema"]?.ToString()
                });
            }

            return result;
        }

        private OpenApiRequestBody? ParseRequestBody(JObject? requestBody)
        {
            if (requestBody == null) return null;

            var content = requestBody["content"] as JObject;
            if (content == null) return null;

            var mediaType = content.Properties().FirstOrDefault();
            if (mediaType == null) return null;

            return new OpenApiRequestBody
            {
                Required = requestBody["required"]?.Value<bool>() ?? false,
                MediaType = mediaType.Name,
                Schema = mediaType.Value["schema"]?.ToString()
            };
        }

        private Dictionary<string, OpenApiResponse> ParseResponses(JObject? responses)
        {
            var result = new Dictionary<string, OpenApiResponse>();
            if (responses == null) return result;

            foreach (var response in responses.Properties())
            {
                var content = response.Value["content"] as JObject;
                if (content == null) continue;

                var mediaType = content.Properties().FirstOrDefault();
                if (mediaType == null) continue;

                result[response.Name] = new OpenApiResponse
                {
                    StatusCode = response.Name,
                    MediaType = mediaType.Name,
                    Schema = mediaType.Value["schema"]?.ToString()
                };
            }

            return result;
        }

        public void SetBearerToken(string token)
        {
            _bearerToken = token;
        }

        public void SetGlobalHeader(string name, string value)
        {
            _globalHeaders[name] = value;
        }

        public async Task<OpenApiResponse<T>> ExecuteAsync<T>(string operationId, object? parameters = null)
        {
            if (!_endpoints.TryGetValue(operationId, out var endpoint))
            {
                throw new OpenApiException($"Operation '{operationId}' not found");
            }

            try
            {
                var request = new RestRequest(endpoint.Path);
                request.Method = (Method)Enum.Parse(typeof(Method), endpoint.Method);

                // Add global headers
                foreach (var header in _globalHeaders)
                {
                    request.AddHeader(header.Key, header.Value);
                }

                // Add bearer token if present
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    request.AddHeader("Authorization", $"Bearer {_bearerToken}");
                }

                // Add parameters
                if (parameters != null)
                {
                    var paramDict = JObject.FromObject(parameters);
                    foreach (var param in endpoint.Parameters)
                    {
                        if (paramDict.TryGetValue(param.Name!, out var value))
                        {
                            switch (param.In?.ToLower())
                            {
                                case "path":
                                    request.AddUrlSegment(param.Name!, value.ToString());
                                    break;
                                case "query":
                                    request.AddQueryParameter(param.Name!, value.ToString());
                                    break;
                                case "header":
                                    request.AddHeader(param.Name!, value.ToString());
                                    break;
                            }
                        }
                        else if (param.Required)
                        {
                            throw new OpenApiException($"Required parameter '{param.Name}' not provided");
                        }
                    }

                    // Add request body if needed
                    if (endpoint.RequestBody != null && endpoint.RequestBody.Required)
                    {
                        request.AddJsonBody(parameters);
                    }
                }

                var response = await _client.ExecuteAsync(request);
                
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(true, null));

                return new OpenApiResponse<T>
                {
                    StatusCode = (int)response.StatusCode,
                    IsSuccessful = response.IsSuccessful,
                    Content = response.IsSuccessful ? 
                        JsonConvert.DeserializeObject<T>(response.Content!) :
                        default,
                    ErrorMessage = response.ErrorMessage
                };
            }
            catch (Exception ex)
            {
                ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(false, ex.Message));
                throw new OpenApiException($"Failed to execute operation '{operationId}'", ex);
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }

    public class OpenApiEndpoint
    {
        public string? Path { get; set; }
        public string? Method { get; set; }
        public string? OperationId { get; set; }
        public List<OpenApiParameter> Parameters { get; set; } = new();
        public OpenApiRequestBody? RequestBody { get; set; }
        public Dictionary<string, OpenApiResponse> Responses { get; set; } = new();
    }

    public class OpenApiParameter
    {
        public string? Name { get; set; }
        public string? In { get; set; }
        public bool Required { get; set; }
        public string? Schema { get; set; }
    }

    public class OpenApiRequestBody
    {
        public bool Required { get; set; }
        public string? MediaType { get; set; }
        public string? Schema { get; set; }
    }

    public class OpenApiResponse
    {
        public string? StatusCode { get; set; }
        public string? MediaType { get; set; }
        public string? Schema { get; set; }
    }

    public class OpenApiResponse<T>
    {
        public int StatusCode { get; set; }
        public bool IsSuccessful { get; set; }
        public T? Content { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class OpenApiException : Exception
    {
        public OpenApiException(string message) : base(message) { }
        public OpenApiException(string message, Exception innerException) : base(message, innerException) { }
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
