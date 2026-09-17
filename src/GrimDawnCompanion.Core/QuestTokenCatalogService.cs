using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

public sealed partial class QuestTokenCatalogService
{
    public Task<IReadOnlyList<QuestTokenRecord>> BuildAsync(GameInstallation game, CancellationToken cancellationToken = default) =>
        Task.Run(() => Build(game, cancellationToken), cancellationToken);

    private static IReadOnlyList<QuestTokenRecord> Build(GameInstallation game, CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, (HashSet<string> Sources, int References)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, path) in FindScriptArchives(game))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scripts = ArcReader.ReadTextEntries(path, name => name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            foreach (var script in scripts.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (Match call in TokenCallRegex().Matches(script))
                {
                    foreach (Match quoted in QuotedValueRegex().Matches(call.Value))
                    {
                        var token = quoted.Groups[1].Value.Trim();
                        if (!LooksLikeToken(token)) continue;
                        if (!records.TryGetValue(token, out var current)) current = (new(StringComparer.OrdinalIgnoreCase), 0);
                        current.Sources.Add(source);
                        records[token] = (current.Sources, current.References + 1);
                    }
                }
            }
        }

        return records.Select(x => new QuestTokenRecord(x.Key, string.Join(", ", x.Value.Sources.Order()), x.Value.References))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<(string Source, string Path)> FindScriptArchives(GameInstallation game)
    {
        var candidates = new (string Source, string RelativePath)[]
        {
            ("Base Game", "resources/Scripts.arc"),
            ("Ashes of Malmouth", "gdx1/resources/Scripts.arc"),
            ("Forgotten Gods", "gdx2/resources/Scripts.arc"),
            ("Fangs of Asterkarn", "gdx3/resources/Scripts.arc"),
            ("The Crucible", "survivalmode1/resources/Scripts.arc"),
            ("The Crucible Update II", "survivalmode2/resources/scripts.arc"),
            ("The Crucible Update III", "survivalmode3/resources/Scripts.arc"),
        };
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(game.RootDirectory, candidate.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) yield return (candidate.Source, path);
        }
    }

    private static bool LooksLikeToken(string value) =>
        value.Length is >= 3 and <= 160 && value.Any(char.IsLetter) &&
        value.All(c => char.IsUpper(c) || char.IsDigit(c) || c is '_' or '-' or ':' or '.');

    [GeneratedRegex(@"(?is)\b(?:GiveTokenToLocalPlayer|GiveTokenIfPlayer|RemoveTokenFromLocalPlayer|HasToken|AnyoneHasToken|ServerHasToken|GiveToken|RemoveToken)\s*\([^\)]{0,300}\)")]
    private static partial Regex TokenCallRegex();

    [GeneratedRegex("[\\\"']([A-Za-z0-9_.:-]+)[\\\"']")]
    private static partial Regex QuotedValueRegex();
}
