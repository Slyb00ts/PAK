# PAK (Protocol Abstraction Kit)

PAK is a .NET library that provides a unified interface for various industrial and network communication protocols. It simplifies the integration of different protocols in your .NET applications by providing consistent and easy-to-use client implementations.

## Supported Protocols

- **BACnet** - Building Automation and Control Networks protocol
- **Modbus** - Serial communications protocol for industrial electronic devices
- **OPC UA** - Open Platform Communications Unified Architecture
- **OpenAPI** - RESTful web services integration
- **SNMP** - Simple Network Management Protocol

## Project Structure

```
PAK/
├── PAK.Bacnet/        # BACnet protocol implementation
├── PAK.Modbus/        # Modbus protocol implementation
├── PAK.OPC/           # OPC UA protocol implementation
├── PAK.OpenAPI/       # OpenAPI/REST client implementation
└── PAK.SNMP/          # SNMP protocol implementation
```

## Requirements

- .NET 9.0 or higher

## Getting Started

1. Add the required protocol package to your project:
```bash
dotnet add package PAK.Bacnet    # For BACnet
dotnet add package PAK.Modbus    # For Modbus
dotnet add package PAK.OPC       # For OPC UA
dotnet add package PAK.OpenAPI   # For OpenAPI
dotnet add package PAK.SNMP      # For SNMP
```

2. Import the desired namespace in your code:
```csharp
using PAK.Bacnet;     // For BACnet
using PAK.Modbus;     // For Modbus
using PAK.OPC;        // For OPC UA
using PAK.OpenAPI;    // For OpenAPI
using PAK.SNMP;       // For SNMP
```

## Usage Examples

### BACnet Client
```csharp
var bacnetClient = new BacnetClient();
// Configure and use BACnet client
```

### Modbus Client
```csharp
var modbusClient = new ModbusClient();
// Configure and use Modbus client
```

### OPC UA Client
```csharp
var opcUaClient = new OpcUaClient();
// Configure and use OPC UA client
```

### OpenAPI Client
```csharp
var openApiClient = new OpenApiClient();
// Configure and use OpenAPI client
```

### SNMP Client
```csharp
var snmpClient = new SnmpClient();
// Configure and use SNMP client
```

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License - see the LICENSE file for details.
