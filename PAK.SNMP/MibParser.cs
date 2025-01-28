using System.Text.RegularExpressions;

namespace PAK.SNMP
{
    public class MibNode
    {
        public string Name { get; set; }
        public string? Oid { get; set; }
        public string? Syntax { get; set; }
        public string? Access { get; set; }
        public string? Status { get; set; }
        public string? Description { get; set; }
        public Dictionary<string, MibNode> Children { get; } = new();
        public MibNode? Parent { get; set; }

        public MibNode(string name)
        {
            Name = name;
        }

        public string GetFullOid()
        {
            if (Parent == null)
                return Oid ?? Name;

            var parentOid = Parent.GetFullOid();
            if (string.IsNullOrEmpty(Oid))
                return parentOid;

            return $"{parentOid}.{Oid}";
        }
    }

    public class MibModule
    {
        public string Name { get; set; }
        public List<string> Imports { get; } = new();
        public Dictionary<string, MibNode> Nodes { get; } = new();
        public Dictionary<string, string> Types { get; } = new();

        public MibModule(string name)
        {
            Name = name;
        }
    }

    public class MibParser
    {
        private readonly Dictionary<string, MibModule> _modules = new();
        private MibModule? _currentModule;
        private static readonly Regex ModuleRegex = new(@"(\w+)\s+DEFINITIONS\s*::=\s*BEGIN", RegexOptions.Compiled);
        private static readonly Regex ImportRegex = new(@"IMPORTS\s+(.*?)\s*;", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex ObjectIdentifierRegex = new(@"(\w+)\s+OBJECT\s+IDENTIFIER\s*::=\s*\{\s*([\w\s\.]+)\s*\}", RegexOptions.Compiled);
        private static readonly Regex ObjectTypeRegex = new(@"(\w+)\s+OBJECT-TYPE\s+SYNTAX\s+([\w\-\(\)\s]+)\s+ACCESS\s+(\w+)\s+STATUS\s+(\w+)\s+DESCRIPTION\s+""([^""]+)""\s*::=\s*\{\s*([\w\s\.]+)\s*\}", RegexOptions.Compiled | RegexOptions.Singleline);

        public Dictionary<string, MibModule> Parse(string filePath)
        {
            var content = File.ReadAllText(filePath);
            ParseContent(content);
            BuildNodeHierarchy();
            return _modules;
        }

        private void ParseContent(string content)
        {
            // Remove comments
            content = Regex.Replace(content, "--.*?(\r?\n|$)", "", RegexOptions.Singleline);

            // Parse module definition
            var moduleMatch = ModuleRegex.Match(content);
            if (!moduleMatch.Success)
                throw new MibParseException("Invalid MIB file format: Missing module definition");

            var moduleName = moduleMatch.Groups[1].Value;
            _currentModule = new MibModule(moduleName);
            _modules[moduleName] = _currentModule;

            // Parse imports
            var importMatch = ImportRegex.Match(content);
            if (importMatch.Success)
            {
                var imports = importMatch.Groups[1].Value
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(i => i.Trim())
                    .Where(i => !string.IsNullOrEmpty(i));

                foreach (var import in imports)
                    _currentModule.Imports.Add(import);
            }

            // Parse object identifiers
            foreach (Match match in ObjectIdentifierRegex.Matches(content))
            {
                var name = match.Groups[1].Value;
                var oidPath = match.Groups[2].Value;

                var node = new MibNode(name)
                {
                    Oid = oidPath.Trim()
                };

                _currentModule.Nodes[name] = node;
            }

            // Parse object types
            foreach (Match match in ObjectTypeRegex.Matches(content))
            {
                var name = match.Groups[1].Value;
                var syntax = match.Groups[2].Value;
                var access = match.Groups[3].Value;
                var status = match.Groups[4].Value;
                var description = match.Groups[5].Value;
                var oidPath = match.Groups[6].Value;

                var node = new MibNode(name)
                {
                    Oid = oidPath.Trim(),
                    Syntax = syntax.Trim(),
                    Access = access,
                    Status = status,
                    Description = description.Trim()
                };

                _currentModule.Nodes[name] = node;
            }
        }

        private void BuildNodeHierarchy()
        {
            foreach (var module in _modules.Values)
            {
                foreach (var node in module.Nodes.Values)
                {
                    if (string.IsNullOrEmpty(node.Oid))
                        continue;

                    var parts = node.Oid.Split('.');
                    MibNode? currentParent = null;

                    foreach (var part in parts)
                    {
                        var trimmedPart = part.Trim();
                        if (module.Nodes.TryGetValue(trimmedPart, out var foundNode))
                        {
                            currentParent = foundNode;
                        }
                        else if (int.TryParse(trimmedPart, out _))
                        {
                            // Numeric OID part
                            continue;
                        }
                        else
                        {
                            // Try to find node in other modules
                            var found = false;
                            foreach (var otherModule in _modules.Values)
                            {
                                if (otherModule.Nodes.TryGetValue(trimmedPart, out foundNode))
                                {
                                    currentParent = foundNode;
                                    found = true;
                                    break;
                                }
                            }

                            if (!found)
                            {
                                // Create placeholder node
                                currentParent = new MibNode(trimmedPart);
                                module.Nodes[trimmedPart] = currentParent;
                            }
                        }
                    }

                    if (currentParent != null && currentParent != node)
                    {
                        node.Parent = currentParent;
                        currentParent.Children[node.Name] = node;
                    }
                }
            }
        }

        public void PrintTree(MibNode node, int level = 0)
        {
            var indent = new string(' ', level * 2);
            Console.WriteLine($"{indent}{node.Name} ({node.GetFullOid()})");
            
            if (!string.IsNullOrEmpty(node.Syntax))
                Console.WriteLine($"{indent}  Syntax: {node.Syntax}");
            if (!string.IsNullOrEmpty(node.Access))
                Console.WriteLine($"{indent}  Access: {node.Access}");
            if (!string.IsNullOrEmpty(node.Status))
                Console.WriteLine($"{indent}  Status: {node.Status}");
            if (!string.IsNullOrEmpty(node.Description))
                Console.WriteLine($"{indent}  Description: {node.Description}");

            foreach (var child in node.Children.Values)
                PrintTree(child, level + 1);
        }
    }

    public class MibParseException : Exception
    {
        public MibParseException(string message) : base(message) { }
        public MibParseException(string message, Exception innerException) : base(message, innerException) { }
    }
}
