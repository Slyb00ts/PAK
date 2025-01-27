namespace PAK.SNMP
{
    public class SnmpData
    {
        internal Lextm.SharpSnmpLib.ISnmpData InternalData { get; }
        public string Value => InternalData.ToString() ?? string.Empty;

        internal SnmpData(Lextm.SharpSnmpLib.ISnmpData data)
        {
            InternalData = data;
        }
    }

    public class SnmpVariable
    {
        public string Oid { get; }
        public SnmpData Value { get; }

        internal SnmpVariable(Lextm.SharpSnmpLib.Variable variable)
        {
            Oid = variable.Id.ToString();
            Value = new SnmpData(variable.Data);
        }
    }
}
