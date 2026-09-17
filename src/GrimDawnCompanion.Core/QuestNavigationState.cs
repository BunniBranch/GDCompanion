using System.Globalization;

namespace GrimDawnCompanion.Core;

/// <summary>A complete, fresh tracked-task snapshot; missing data never promotes old quest stages.</summary>
public sealed class QuestNavigationState
{
    public Dictionary<string, HashSet<uint>> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Tokens { get; } = new(StringComparer.Ordinal);
    public string Signature => string.Join(';', Tasks.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => pair.Key + ":" + string.Join(',', pair.Value.Order()))) + "|" +
        string.Join(';', Tokens.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));

    public bool Matches(QuestTargetBinding binding)
    {
        var path = QuestNavigationIndex.Normalize(binding.QuestPath);
        return Tasks.Any(pair => pair.Value.Contains(binding.TaskUid) &&
            (pair.Key.Equals(path, StringComparison.OrdinalIgnoreCase) ||
             pair.Key.EndsWith('/' + path, StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith('/' + pair.Key, StringComparison.OrdinalIgnoreCase)));
    }

    public bool Allows(WorldMarkerRecord marker) => marker.QuestTargets?.Any(Matches) == true &&
        (marker.RequiredTokens ?? []).All(token => Tokens.TryGetValue(token, out var present) && present) &&
        (marker.ExcludedTokens ?? []).All(token => Tokens.TryGetValue(token, out var present) && !present);

    public IEnumerable<string> NeededTokens(IEnumerable<WorldMarkerRecord> markers) => markers
        .Where(marker => marker.QuestTargets?.Any(Matches) == true)
        .SelectMany(marker => (marker.RequiredTokens ?? []).Concat(marker.ExcludedTokens ?? []))
        .Distinct(StringComparer.Ordinal);

    public static QuestNavigationState? Parse(string response)
    {
        if (response == "OK QUEST_TASKS WAITING") return null;
        var fields = response.Split('\t');
        const string prefix = "OK QUEST_TASKS ";
        if (!fields[0].StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(fields[0][prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count < 0 || count > 128 || count != fields.Length - 1)
            throw new InvalidDataException("Invalid quest-stage snapshot.");
        var state = new QuestNavigationState();
        foreach (var field in fields.Skip(1))
        {
            var parts = field.Split('|');
            if (parts.Length != 2 || !parts[0].EndsWith(".qst", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid quest-stage entry.");
            var tasks = new HashSet<uint>();
            if (parts[1].Length > 0)
                foreach (var value in parts[1].Split(','))
                {
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
                        throw new InvalidDataException("Invalid quest task identifier.");
                    tasks.Add(uid);
                }
            if (!state.Tasks.TryAdd(QuestNavigationIndex.Normalize(parts[0]), tasks))
                throw new InvalidDataException("Duplicate quest-stage entry.");
        }
        return state;
    }
}
