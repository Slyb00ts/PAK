namespace PAK.SNMP
{
    public class SnmpTrapEventArgs : EventArgs
    {
        public string Enterprise { get; }
        public string AgentAddress { get; }
        public int GenericTrap { get; }
        public int SpecificTrap { get; }
        public uint TimeStamp { get; }
        public IList<SnmpVariable> Variables { get; }

        internal SnmpTrapEventArgs(string enterprise, string agentAddress, int genericTrap, 
            int specificTrap, uint timeStamp, IList<Lextm.SharpSnmpLib.Variable> variables)
        {
            Enterprise = enterprise;
            AgentAddress = agentAddress;
            GenericTrap = genericTrap;
            SpecificTrap = specificTrap;
            TimeStamp = timeStamp;
            Variables = variables.Select(v => new SnmpVariable(v)).ToList();
        }
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
