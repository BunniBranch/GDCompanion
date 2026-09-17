using System.Globalization;

namespace GrimDawnCompanion.Core;

// Transport-independent so timeout, disconnect and no-retry behavior can be
// exercised without a running game or a character/save mutation.
public static class BookmarkTravelWorkflow
{
    public const string UnknownOutcome = "Travel could not be verified. Grim Dawn may still complete travel already started, even if Companion disconnects. Check your character in-game before any further travel. No automatic retry was made.";

    public static async Task RunAsync(string startCommand, Func<string, CancellationToken, Task<string>> send,
        Action<string>? progress = null, CancellationToken ct = default,
        TimeSpan? pollInterval = null, TimeSpan? verificationTimeout = null)
    {
        ct.ThrowIfCancellationRequested();
        string response;
        try { response = await send(startCommand, ct); }
        catch (Exception ex) { throw new InvalidOperationException(UnknownOutcome, ex); }
        if (response == "OK BOOKMARK RETURNED") return;
        if (!TryLoadingToken(response, out var token)) throw new InvalidOperationException(GameBridgeClient.BookmarkError(response));
        try
        {
            progress?.Invoke("Loading destination… Grim Dawn is handling travel. Please wait.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(verificationTimeout ?? TimeSpan.FromSeconds(120));
            var interval = pollInterval ?? TimeSpan.FromMilliseconds(300);
            while (true)
            {
                await Task.Delay(interval, timeout.Token);
                // Only STATUS is repeated. Never repeat the travel submission,
                // including when the game stops servicing the bridge while loading.
                response = await send("BOOKMARK\tSTATUS " + token.ToString(CultureInfo.InvariantCulture), timeout.Token);
                if (response == "OK BOOKMARK RETURNED") return;
                if (response == "ERROR GAME_THREAD_TIMEOUT") continue;
                if (!TryLoadingToken(response, out var returned) || returned != token)
                    throw new InvalidOperationException(UnknownOutcome);
            }
        }
        catch (Exception ex) { throw new InvalidOperationException(UnknownOutcome, ex); }
    }

    public static bool TryLoadingToken(string response, out ulong token)
    {
        const string prefix = "OK BOOKMARK LOADING ";
        token = 0;
        return response.StartsWith(prefix, StringComparison.Ordinal) &&
            ulong.TryParse(response.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out token) && token != 0;
    }
}
