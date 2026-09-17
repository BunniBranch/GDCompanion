using System.Globalization;
using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

/// <summary>Read-only, data-driven quest placement relationships. Never executes game scripts.</summary>
public static class QuestNavigationIndex
{
    public sealed record Target(string RecordPath, string? Name, QuestTargetBinding[] Bindings,
        string[] RequiredTokens, string[] ExcludedTokens);

    public static QuestNavigationRecord? ReadRecord(string path, IEnumerable<string> lines,
        IReadOnlyDictionary<string, string> tags)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var comma = line.IndexOf(',');
            if (comma <= 0) continue;
            fields[line[..comma]] = line[(comma + 1)..].TrimEnd(',').Trim();
        }
        var bindings = new List<QuestTargetBinding>();
        foreach (var (key, value) in fields.Where(pair => pair.Key.StartsWith("questFile", StringComparison.OrdinalIgnoreCase)))
        {
            if (!value.EndsWith(".qst", StringComparison.OrdinalIgnoreCase) ||
                !fields.TryGetValue("taskUID" + key[9..], out var rawUid) ||
                !long.TryParse(rawUid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid) ||
                uid < int.MinValue || uid > uint.MaxValue || uid == 0) continue;
            bindings.Add(new(Normalize(value), unchecked((uint)uid)));
        }
        var children = new List<string>();
        var kind = fields.GetValueOrDefault("Class", "");
        if (kind.Length == 0 && Normalize(fields.GetValueOrDefault("templateName", "")).EndsWith("/proxypool.tpl", StringComparison.Ordinal))
            kind = "ProxyPool";
        // Follow only spawn relationships, never loot/equipment/controller references.
        var childPattern = kind.Equals("Proxy", StringComparison.OrdinalIgnoreCase) ? @"^pool(?:Epic|Legendary)?\d+$" :
            kind.Equals("ProxyPool", StringComparison.OrdinalIgnoreCase) ? @"^name(?:Champion)?\d+$" : null;
        if (childPattern is not null)
            foreach (var (key, value) in fields)
                if (Regex.IsMatch(key, childPattern, RegexOptions.IgnoreCase) &&
                    value.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase))
                    children.Add(Normalize(value));
        var onAdd = kind.Equals("ScriptEntity", StringComparison.OrdinalIgnoreCase)
            ? fields.GetValueOrDefault("onAddToWorld") : null;
        if (bindings.Count == 0 && children.Count == 0 && string.IsNullOrWhiteSpace(onAdd)) return null;
        var nameTag = fields.GetValueOrDefault("description", fields.GetValueOrDefault("AreaDescription", ""));
        return new(Normalize(path), tags.GetValueOrDefault(nameTag), bindings.Distinct().ToArray(),
            children.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), onAdd);
    }

    public static Dictionary<string, Target[]> Resolve(IReadOnlyDictionary<string, QuestNavigationRecord> records,
        IEnumerable<string> scripts)
    {
        var normalized = records.Values.ToDictionary(record => Normalize(record.RecordPath), StringComparer.OrdinalIgnoreCase);
        var functions = new Dictionary<string, QuestSpawnLink[]>(StringComparer.Ordinal);
        foreach (var script in scripts)
            foreach (var (function, links) in ReadScriptSpawns(script)) functions[function] = links;
        var result = new Dictionary<string, Target[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in normalized.Keys)
        {
            var targets = Visit(key, [], [], new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0).Distinct().ToArray();
            if (targets.Length > 0) result[key] = targets;
        }
        return result;

        IEnumerable<Target> Visit(string key, string[] required, string[] excluded, HashSet<string> visited, int depth)
        {
            if (depth > 12 || !visited.Add(key) || !normalized.TryGetValue(key, out var record)) yield break;
            if (record.Targets.Length > 0) yield return new(record.RecordPath, record.DisplayName, record.Targets, required, excluded);
            var links = record.Children.Select(child => new QuestSpawnLink(child, [], []));
            if (record.OnAddToWorld is not null && functions.TryGetValue(record.OnAddToWorld, out var scripted))
                links = links.Concat(scripted);
            foreach (var link in links)
            {
                var nextRequired = required.Concat(link.RequiredTokens).Distinct(StringComparer.Ordinal).ToArray();
                var nextExcluded = excluded.Concat(link.ExcludedTokens).Distinct(StringComparer.Ordinal).ToArray();
                if (nextRequired.Intersect(nextExcluded, StringComparer.Ordinal).Any()) continue;
                foreach (var target in Visit(link.RecordPath, nextRequired, nextExcluded, new(visited, StringComparer.OrdinalIgnoreCase), depth + 1))
                    yield return target;
            }
        }
    }

    public static Dictionary<string, QuestSpawnLink[]> ReadScriptSpawns(string source)
    {
        // Recognize the game's ordered token-state swap idiom. Unsupported Lua
        // logic remains live-only; never infer a spawn from an arbitrary string.
        var text = Regex.Replace(source, @"/\*[\s\S]*?\*/|--\[\[[\s\S]*?\]\]|--[^\r\n]*", "");
        var tables = new Dictionary<string, QuestSpawnLink[]>(StringComparer.Ordinal);
        foreach (Match declaration in Regex.Matches(text, @"local\s+(\w+)\s*=\s*orderedTable\s*\(\s*\)"))
        {
            var name = declaration.Groups[1].Value;
            var rows = Regex.Matches(text, @"\b" + Regex.Escape(name) + "\\s*\\[\\s*\"([^\"]*)\"\\s*\\]\\s*=\\s*\\{([^{}]*)\\}");
            var previous = new List<string>();
            var links = new List<QuestSpawnLink>();
            var valid = rows.Count > 0 && Regex.Matches(text, @"\b" + Regex.Escape(name) + @"\s*\[[^\]]*\]\s*=").Count == rows.Count;
            foreach (Match row in rows)
            {
                var token = row.Groups[1].Value;
                if (!Regex.IsMatch(token, @"^[A-Za-z0-9_]*$")) { valid = false; break; }
                var dbr = Regex.Match(row.Groups[2].Value, "\\bdbr\\s*=\\s*(?:\"([^\"]+\\.dbr)\"|(nil))\\s*(?:,|$)");
                if (!dbr.Success) { valid = false; break; }
                if (dbr.Groups[1].Success)
                    links.Add(new(Normalize(dbr.Groups[1].Value), token.Length == 0 ? [] : [token], previous.ToArray()));
                if (token.Length == 0) break;
                previous.Add(token);
            }
            if (valid) tables[name] = links.ToArray();
        }
        var result = new Dictionary<string, QuestSpawnLink[]>(StringComparer.Ordinal);
        var definitions = Regex.Matches(text, @"\bfunction\s+([\w.]+)\s*\(([^)]*)\)");
        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            var end = i + 1 < definitions.Count ? definitions[i + 1].Index : text.Length;
            var body = text[(definition.Index + definition.Length)..end];
            var firstArgument = definition.Groups[2].Value.Split(',')[0].Trim();
            // Match only a swap anchored at this callback's object. Custom
            // coordinates, helper wrappers and multiplayer overrides need a
            // separate resolver, not a guessed point at the script anchor.
            var calls = Regex.Matches(body, @"TokenStateBasedObjectSwap\s*\(\s*(\w+)\s*,\s*\w+\s*,\s*(\w+)\s*\)");
            var beforeSwap = calls.Count == 1 ? body[..calls[0].Index] : body;
            var conditions = Regex.Matches(beforeSwap, @"\b(?:if|elseif)\s+(.*?)\s+then", RegexOptions.Singleline);
            if (calls.Count == 1 && calls[0].Groups[1].Value == firstArgument &&
                conditions.Cast<Match>().All(condition => condition.Groups[1].Value.Trim() == "Server") &&
                !Regex.IsMatch(beforeSwap, @"\b(?:for|while|repeat|return)\b") &&
                !body.Contains("SetCoords", StringComparison.Ordinal) &&
                tables.TryGetValue(calls[0].Groups[2].Value, out var links))
                result[definition.Groups[1].Value] = links;
        }
        return result;
    }

    public static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
}
