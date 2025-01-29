using System.Text;

namespace PAK.SNMP
{
    public class MibVariable
    {
        public string Name { get; set; }
        public string FullOid { get; set; }
        public string Type { get; set; }
        public string? Access { get; set; }
        public string? Status { get; set; }
        public string? Description { get; set; }
        public string? Units { get; set; }
        public string? DisplayHint { get; set; }
        public Dictionary<string, string> EnumValues { get; } = new();

        public MibVariable(string name, string fullOid, string type)
        {
            Name = name;
            FullOid = fullOid;
            Type = type;
        }

        public override string ToString()
        {
            var result = $"{Name} {FullOid} {Type}";
            if (EnumValues.Count > 0)
            {
                result += " {";
                result += string.Join(", ", EnumValues.Select(kv => $"{kv.Key} ({kv.Value})"));
                result += "}";
            }
            if (!string.IsNullOrEmpty(Description))
                result += $"\nDescription: {Description}";
            if (!string.IsNullOrEmpty(Units))
                result += $"\nUnits: {Units}";
            if (!string.IsNullOrEmpty(DisplayHint))
                result += $"\nDisplayHint: {DisplayHint}";
            return result;
        }
    }

    public class MibParser
    {
        private class MibDefinition
        {
            public string Name { get; set; }
            public string Parent { get; set; }
            public string Index { get; set; }
            public Dictionary<string, string> Attributes { get; } = new();
        }

        private readonly Dictionary<string, string> _smiOids = new()
        {
            ["iso"] = "1",
            ["org"] = "1.3",
            ["dod"] = "1.3.6",
            ["internet"] = "1.3.6.1",
            ["directory"] = "1.3.6.1.1",
            ["mgmt"] = "1.3.6.1.2",
            ["mib-2"] = "1.3.6.1.2.1",
            ["transmission"] = "1.3.6.1.2.1.10",
            ["experimental"] = "1.3.6.1.3",
            ["private"] = "1.3.6.1.4",
            ["enterprises"] = "1.3.6.1.4.1",
            ["security"] = "1.3.6.1.5",
            ["snmpV2"] = "1.3.6.1.6"
        };

        private readonly HashSet<string> _wellKnownTypes = new()
        {
            "MODULE-IDENTITY", "OBJECT-TYPE", "NOTIFICATION-TYPE",
            "OBJECT-GROUP", "NOTIFICATION-GROUP", "MODULE-COMPLIANCE",
            "TEXTUAL-CONVENTION", "OBJECT IDENTIFIER"
        };

        public List<MibVariable> ParseString(string content)
        {
            var oidMap = new Dictionary<string, string>(_smiOids);
            var definitions = new Dictionary<string, MibDefinition>();
            var lines = content.Split('\n');
            var currentDef = new MibDefinition();
            var moduleIdentity = "";
            var inImports = false;
            var importedOids = new HashSet<string>();

            // Pierwszy przebieg - szukamy IMPORTS i MODULE-IDENTITY
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("--"))
                    continue;

                if (trimmedLine == "IMPORTS")
                {
                    inImports = true;
                    continue;
                }

                if (inImports)
                {
                    if (trimmedLine.EndsWith(";"))
                    {
                        inImports = false;
                        continue;
                    }

                    // Zbieramy importowane nazwy aż do FROM
                    if (!trimmedLine.Contains("FROM"))
                    {
                        foreach (var name in trimmedLine.Split(','))
                        {
                            var cleanName = name.Trim();
                            if (!string.IsNullOrEmpty(cleanName))
                            {
                                importedOids.Add(cleanName);
                                // Jeśli importowany OID jest w bazie znanych OID-ów, dodajemy go do mapy
                                if (_smiOids.ContainsKey(cleanName))
                                {
                                    oidMap[cleanName] = _smiOids[cleanName];
                                }
                            }
                        }
                    }
                    else
                    {
                        var parts = trimmedLine.Split(new[] { "FROM" }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            var importedNames = parts[0];
                            foreach (var name in importedNames.Split(','))
                            {
                                var cleanName = name.Trim();
                                if (!string.IsNullOrEmpty(cleanName))
                                {
                                    importedOids.Add(cleanName);
                                    // Jeśli importowany OID jest w bazie znanych OID-ów, dodajemy go do mapy
                                    if (_smiOids.ContainsKey(cleanName))
                                    {
                                        oidMap[cleanName] = _smiOids[cleanName];
                                    }
                                }
                            }
                        }
                    }
                    continue;
                }

                if (trimmedLine.EndsWith("MODULE-IDENTITY"))
                {
                    moduleIdentity = trimmedLine.Split(' ')[0];
                }
                else if (!string.IsNullOrEmpty(moduleIdentity) && trimmedLine.StartsWith("::=") && trimmedLine.Contains("{") && importedOids.Contains("enterprises"))
                {
                    var parts = trimmedLine.Split(new[] { '{', '}' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var definition = parts[1].Trim().Split(' ');
                        if (definition.Length >= 2 && definition[0].Trim() == "enterprises")
                        {
                            var enterpriseId = definition[1].Trim();
                            oidMap[moduleIdentity] = oidMap["enterprises"] + "." + enterpriseId;
                        }
                    }
                }
            }

            // Drugi przebieg - zbieramy wszystkie definicje
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("--"))
                    continue;

                if (trimmedLine.Contains("OBJECT IDENTIFIER") && trimmedLine.Contains("::="))
                {
                    var name = trimmedLine.Split(new[] { "OBJECT", "IDENTIFIER", "::=" }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    var parts = trimmedLine.Split(new[] { "::=" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var value = parts[1].Trim();
                        // Sprawdzamy czy to bezpośredni OID (np. "1.3.6.1.4.1.55108")
                        if (!value.Contains("{") && value.All(c => char.IsDigit(c) || c == '.'))
                        {
                            oidMap[name] = value.TrimStart('.');
                        }
                        // Sprawdzamy czy to referencja z indeksem (np. "enterprises.55108")
                        else if (!value.Contains("{") && value.Contains("."))
                        {
                            var refParts = value.Split('.');
                            if (refParts.Length >= 2 && oidMap.ContainsKey(refParts[0]))
                            {
                                var baseOid = oidMap[refParts[0]];
                                var index = string.Join(".", refParts.Skip(1));
                                oidMap[name] = baseOid + "." + index;
                            }
                        }
                        // Sprawdzamy czy to referencja do innego OID-a (np. "mgmt")
                        else if (!value.Contains("{") && !value.Contains(" "))
                        {
                            var refName = value.Trim();
                            if (oidMap.ContainsKey(refName))
                            {
                                oidMap[name] = oidMap[refName];
                            }
                        }
                        // Jeśli to definicja z klamrami (np. "{ enterprises 55108 }")
                        else if (value.Contains("{"))
                        {
                            var bracketParts = value.Split(new[] { '{', '}' }, StringSplitOptions.RemoveEmptyEntries);
                            if (bracketParts.Length >= 1)
                            {
                                var definition = bracketParts[0].Trim().Split(' ');
                                if (definition.Length >= 2)
                                {
                                    var parent = definition[0].Trim();
                                    var index = definition[1].Trim();
                                    definitions[name] = new MibDefinition { Name = name, Parent = parent, Index = index };
                                }
                            }
                        }
                    }
                    continue;
                }

                if (trimmedLine.EndsWith("OBJECT-TYPE"))
                {
                    if (!string.IsNullOrEmpty(currentDef.Name))
                    {
                        definitions[currentDef.Name] = currentDef;
                    }
                    currentDef = new MibDefinition { Name = trimmedLine.Split(' ')[0] };
                    continue;
                }

                if (trimmedLine.StartsWith("::="))
                {
                    var value = trimmedLine.Substring(3).Trim();
                    // Sprawdzamy czy to bezpośredni OID (np. "1.3.6.1.4.1.55108.2")
                    if (!value.Contains("{") && value.All(c => char.IsDigit(c) || c == '.'))
                    {
                        oidMap[currentDef.Name] = value.TrimStart('.');
                    }
                    // Sprawdzamy czy to referencja z indeksem (np. "enterprises.55108")
                    else if (!value.Contains("{") && value.Contains("."))
                    {
                        var refParts = value.Split('.');
                        if (refParts.Length >= 2 && oidMap.ContainsKey(refParts[0]))
                        {
                            var baseOid = oidMap[refParts[0]];
                            var index = string.Join(".", refParts.Skip(1));
                            oidMap[currentDef.Name] = baseOid + "." + index;
                        }
                    }
                    // Sprawdzamy czy to referencja do innego OID-a (np. "mgmt")
                    else if (!value.Contains("{") && !value.Contains(" "))
                    {
                        var refName = value.Trim();
                        if (oidMap.ContainsKey(refName))
                        {
                            oidMap[currentDef.Name] = oidMap[refName];
                        }
                    }
                    // Jeśli to definicja z klamrami (np. "{ enterprises 55108 }")
                    else if (value.Contains("{"))
                    {
                        var oidParts = value.Split(new[] { '{', '}' }, StringSplitOptions.RemoveEmptyEntries);
                        if (oidParts.Length > 0)
                        {
                            var oidDefinition = oidParts[0].Trim().Split(' ');
                            if (oidDefinition.Length >= 2)
                            {
                                currentDef.Parent = oidDefinition[0].Trim();
                                currentDef.Index = oidDefinition[1].Trim();
                            }
                        }
                    }
                    continue;
                }

                if (trimmedLine.StartsWith("SYNTAX"))
                    currentDef.Attributes["SYNTAX"] = ExtractValue(trimmedLine, "SYNTAX");
                else if (trimmedLine.StartsWith("ACCESS") || trimmedLine.StartsWith("MAX-ACCESS"))
                    currentDef.Attributes["ACCESS"] = ExtractValue(trimmedLine, trimmedLine.StartsWith("ACCESS") ? "ACCESS" : "MAX-ACCESS");
                else if (trimmedLine.StartsWith("STATUS"))
                    currentDef.Attributes["STATUS"] = ExtractValue(trimmedLine, "STATUS");
                else if (trimmedLine.StartsWith("DESCRIPTION"))
                    currentDef.Attributes["DESCRIPTION"] = ExtractQuotedValue(trimmedLine, content, ref lines);
                else if (trimmedLine.StartsWith("UNITS"))
                    currentDef.Attributes["UNITS"] = ExtractQuotedValue(trimmedLine, content, ref lines);
            }

            if (!string.IsNullOrEmpty(currentDef.Name))
            {
                definitions[currentDef.Name] = currentDef;
            }

            // Czwarty przebieg - budujemy zmienne
            var variables = new List<MibVariable>();

            // Najpierw dodajemy zmienne z bezpośrednimi OID-ami
            foreach (var def in definitions.Values)
            {
                if (oidMap.TryGetValue(def.Name, out var directOid))
                {
                    variables.Add(CreateMibVariable(def, directOid));
                }
                else
                {
                    var fullOid = BuildFullOid(def, oidMap, definitions);
                    if (!string.IsNullOrEmpty(fullOid))
                    {
                        variables.Add(CreateMibVariable(def, fullOid));
                    }
                }
            }

            return variables.OrderBy(v => v.FullOid).ToList();
        }

        private string BuildFullOid(MibDefinition def, Dictionary<string, string> oidMap, Dictionary<string, MibDefinition> definitions, HashSet<string> visited = null)
        {
            if (string.IsNullOrEmpty(def.Parent) || string.IsNullOrEmpty(def.Index))
                return null;

            visited ??= new HashSet<string>();

            // Wykrywanie cykli
            if (!visited.Add(def.Name))
                return null;

            // Jeśli już mamy obliczony OID dla tego węzła, zwracamy go
            if (oidMap.TryGetValue(def.Name, out var existingOid))
                return existingOid;

            // Jeśli parent jest w bazie znanych OID-ów
            if (oidMap.TryGetValue(def.Parent, out var parentOid))
            {
                var fullOid = parentOid + "." + def.Index;
                oidMap[def.Name] = fullOid;
                return fullOid;
            }

            // Jeśli parent jest w definicjach, rekurencyjnie budujemy jego OID
            if (definitions.TryGetValue(def.Parent, out var parentDef))
            {
                var parentOidStr = BuildFullOid(parentDef, oidMap, definitions, visited);
                if (!string.IsNullOrEmpty(parentOidStr))
                {
                    var fullOid = parentOidStr + "." + def.Index;
                    oidMap[def.Name] = fullOid;
                    return fullOid;
                }
            }

            return null;
        }

        private MibVariable CreateMibVariable(MibDefinition def, string fullOid)
        {
            var type = def.Attributes.GetValueOrDefault("SYNTAX", "Unknown");
            var variable = new MibVariable(def.Name, "." + fullOid, type)
            {
                Access = def.Attributes.GetValueOrDefault("ACCESS"),
                Status = def.Attributes.GetValueOrDefault("STATUS"),
                Description = def.Attributes.GetValueOrDefault("DESCRIPTION"),
                Units = def.Attributes.GetValueOrDefault("UNITS")
            };

            // Sprawdzamy czy SYNTAX zawiera wartości enum
            if (type.Contains("{"))
            {
                var enumStart = type.IndexOf('{');
                var enumEnd = type.LastIndexOf('}');
                if (enumStart != -1 && enumEnd != -1)
                {
                    var enumContent = type.Substring(enumStart + 1, enumEnd - enumStart - 1);
                    var enumPairs = enumContent.Split(',');
                    foreach (var pair in enumPairs)
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(pair.Trim(), @"(\w+)\s*\((\d+)\)");
                        if (match.Success)
                        {
                            variable.EnumValues[match.Groups[2].Value] = match.Groups[1].Value;
                        }
                    }
                }
            }

            return variable;
        }

        private string ExtractValue(string line, string keyword)
        {
            var value = line.Substring(keyword.Length).Trim();
            return value.TrimEnd(new[] { ' ', '\t', '\r', '\n', ',' });
        }

        private string ExtractQuotedValue(string firstLine, string fullContent, ref string[] lines)
        {
            var startQuote = firstLine.IndexOf('"');
            if (startQuote == -1) return "";

            var value = firstLine.Substring(startQuote + 1);
            if (value.EndsWith("\""))
                return value.TrimEnd('"');

            var sb = new StringBuilder(value);
            var currentLineIndex = Array.IndexOf(lines, firstLine) + 1;

            while (currentLineIndex < lines.Length)
            {
                var line = lines[currentLineIndex].Trim();
                if (line.EndsWith("\""))
                {
                    sb.Append(" ").Append(line.TrimEnd('"'));
                    break;
                }
                sb.Append(" ").Append(line);
                currentLineIndex++;
            }

            return sb.ToString();
        }

        public List<MibVariable> ParseFile(string filePath)
        {
            var content = File.ReadAllText(filePath);
            return ParseString(content);
        }
    }
}
