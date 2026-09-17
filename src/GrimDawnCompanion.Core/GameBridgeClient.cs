using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Globalization;

namespace GrimDawnCompanion.Core;

public sealed partial class GameBridgeClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    public int? ProcessId { get; private set; }
    public bool IsConnected => _pipe?.IsConnected == true;
    public bool IsGameProcessRunning => ProcessId is int id && IsProcessRunning(id);
    public int ProtocolVersion { get; private set; }
    public IReadOnlySet<string> Capabilities { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool SupportsUtilities => ProtocolVersion >= 2 && Capabilities.Contains("LUA_SYNC");
    public bool SupportsNavigation => ProtocolVersion >= 4 && Capabilities.Contains("NAVIGATION");
    public bool SupportsPosition => ProtocolVersion >= 13 && Capabilities.Contains("POSITION");
    public bool SupportsRegion => ProtocolVersion >= 18 && Capabilities.Contains("REGION");
    public bool SupportsMinimapGeometry => ProtocolVersion >= 19 && Capabilities.Contains("MINIMAP");
    public bool SupportsMinimapProjection => ProtocolVersion >= 23 && SupportsMinimapGeometry && Capabilities.Contains("MINIMAP_PROJECTION");
    public bool SupportsNavigationOverlay => ProtocolVersion >= 16 && SupportsPosition && Capabilities.Contains("QUESTS") && Capabilities.Contains("RADAR");
    public bool SupportsQuestTasks => ProtocolVersion >= 30 && Capabilities.Contains("QUEST_TASKS");
    public bool SupportsLiveQuestTasks => ProtocolVersion >= 33 && SupportsQuestTasks && Capabilities.Contains("QUEST_TASKS_LIVE");
    public bool SupportsCharacterLevel => ProtocolVersion >= 36 && Capabilities.Contains("CHARACTER_LEVEL") && SupportsResourceSet;
    public bool SupportsNativeRadar => ProtocolVersion >= 31 && SupportsMinimapProjection && Capabilities.Contains("RADAR_CANVAS");

    public async Task<string> ConnectAsync(GameInstallation game, string bridgeDllPath, CancellationToken cancellationToken = default)
    {
        ValidateNativeApiBindings();
        if (!File.Exists(bridgeDllPath)) throw new FileNotFoundException("The native game bridge is missing.", bridgeDllPath);
        var processes = Process.GetProcessesByName("Grim Dawn");
        var process = processes.FirstOrDefault(p =>
        {
            try { return string.Equals(Path.GetFullPath(p.MainModule!.FileName), Path.GetFullPath(game.ExecutablePath), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }) ?? throw new InvalidOperationException("Grim Dawn x64 is not running from the configured game directory.");
        ProcessId = process.Id;

        if (!await TryOpenPipeAsync(process.Id, 250, cancellationToken))
        {
            await Task.Run(() => Inject(process, Path.GetFullPath(bridgeDllPath)), cancellationToken);
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline && !await TryOpenPipeAsync(process.Id, 500, cancellationToken))
                await Task.Delay(200, cancellationToken);
        }
        if (!IsConnected) throw new InvalidOperationException("The bridge loaded, but its command channel did not become ready.");
        var response = await SendAsync("PING", cancellationToken);
        if (!response.StartsWith("PONG", StringComparison.Ordinal)) throw new InvalidOperationException("The game bridge returned an unexpected handshake.");
        ParseHandshake(response);
        return response;
    }

    private void ParseHandshake(string response)
    {
        var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ProtocolVersion = parts.Length > 1 && int.TryParse(parts[1], out var version) ? version : 1;
        var capabilityPart = parts.FirstOrDefault(x => x.StartsWith("CAPS=", StringComparison.OrdinalIgnoreCase));
        Capabilities = new HashSet<string>(capabilityPart?[5..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public static void ValidateNativeApiBindings()
    {
        var kernel = GetModuleHandle("kernel32.dll");
        if (kernel == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve kernel32.dll.");
        if (GetProcAddress(kernel, "LoadLibraryW") == 0)
            throw new InvalidOperationException("Windows LoadLibraryW is unavailable.");
    }

    public async Task<string> SpawnItemAsync(ItemRecord item, int quantity, bool completeComponent, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Connect to Grim Dawn before spawning an item.");
        quantity = Math.Clamp(quantity, 1, 1000);
        var path = item.RecordPath.Replace("\\", "/").Replace("\"", "\\\"");
        var lua = $"local p=Game.GetLocalPlayer(); if p~=nil then p:GiveItem(\"{path}\",{quantity},{completeComponent.ToString().ToLowerInvariant()}) end";
        return await SendAsync("LUA\t" + lua, cancellationToken);
    }

    public async Task<string> RunLuaAsync(string script, CancellationToken cancellationToken = default) =>
        await SendAsync("LUA\t" + script.Replace('\r', ' ').Replace('\n', ' '), cancellationToken);

    public async Task<bool> UnloadAsync(CancellationToken cancellationToken = default)
    {
        var processId = ProcessId;
        if (processId is int exitedId && !IsProcessRunning(exitedId))
        {
            DisconnectLocally();
            return true;
        }
        if (!IsConnected || ProtocolVersion < 3 || !Capabilities.Contains("UNLOAD")) return false;
        if (SupportsGameplayAssists) await StopGameplayAssistsAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var response = await SendAsync("SHUTDOWN", timeout.Token);
            var accepted = string.Equals(response, "OK UNLOADING", StringComparison.Ordinal);
            AbortPipe();
            if (accepted && processId is int id) await WaitForBridgeUnloadAsync(id, timeout.Token);
            ResetConnectionState();
            return accepted;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var gameExited = processId is int id && !IsProcessRunning(id);
            DisconnectLocally();
            return gameExited;
        }
        catch (IOException)
        {
            var gameExited = processId is int id && !IsProcessRunning(id);
            DisconnectLocally();
            return gameExited;
        }
    }

    private static async Task WaitForBridgeUnloadAsync(int processId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited || !process.Modules.Cast<ProcessModule>().Any(module =>
                        string.Equals(module.ModuleName, "GrimDawnBridge.dll", StringComparison.OrdinalIgnoreCase)))
                    return;
            }
            catch (ArgumentException) { return; }
            catch (System.ComponentModel.Win32Exception)
            {
                // Module enumeration can be restricted across elevation
                // boundaries. A conservative delay still prevents an
                // immediate reconnect from racing native hook cleanup.
                await Task.Delay(750, cancellationToken);
                return;
            }
            await Task.Delay(75, cancellationToken);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public async Task<CharacterResources> GetResourcesAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability("RESOURCES");
        return ParseResources(await SendAsync("RESOURCE\tGET", cancellationToken));
    }

    public async Task<bool> HasTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        EnsureCapability("TOKENS");
        token = ValidateToken(token);
        var response = await SendAsync("TOKEN\tHAS\t" + token, cancellationToken);
        if (response == "OK TOKEN PRESENT") return true;
        if (response == "OK TOKEN ABSENT") return false;
        throw new InvalidOperationException(response);
    }

    public async Task<bool> SetTokenAsync(string token, bool present, CancellationToken cancellationToken = default)
    {
        EnsureCapability("TOKENS"); EnsureCapability("LUA_SYNC");
        token = ValidateToken(token);
        var action = present
            ? $"if not p:HasToken(\"{token}\") then p:GiveToken(\"{token}\") end"
            : $"if p:HasToken(\"{token}\") then p:RemoveToken(\"{token}\") end";
        var response = await RunLuaSynchronousAsync($"local p=Game.GetLocalPlayer(); if p~=nil then {action} end", cancellationToken);
        if (!response.StartsWith("OK", StringComparison.Ordinal)) throw new InvalidOperationException(response);
        return await HasTokenAsync(token, cancellationToken);
    }

    public async Task SetNavigationAsync(bool activeQuests, bool pointsOfInterest, CancellationToken cancellationToken = default)
    {
        EnsureCapability("NAVIGATION");
        if (!activeQuests && !pointsOfInterest) throw new ArgumentException("Select at least one marker category.");
        var response = await SendAsync($"NAVIGATION\tSET\t{(activeQuests ? 1 : 0)}\t{(pointsOfInterest ? 1 : 0)}", cancellationToken);
        if (!response.StartsWith("OK NAVIGATION", StringComparison.Ordinal)) throw new InvalidOperationException(response);
    }

    public async Task ClearNavigationAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability("NAVIGATION");
        var response = await SendAsync("NAVIGATION\tCLEAR", cancellationToken);
        if (!response.StartsWith("OK NAVIGATION", StringComparison.Ordinal)) throw new InvalidOperationException(response);
    }

    public async Task ReplaceWorldMarkersAsync(IEnumerable<WorldMarkerRecord> markers, bool useNativeKinds = true,
        CancellationToken cancellationToken = default)
    {
        EnsureCapability("NAVIGATION");
        var clear = await SendAsync("NAVIGATION\tMARKERS\tCLEAR", cancellationToken);
        if (!clear.StartsWith("OK NAVIGATION", StringComparison.Ordinal)) throw new InvalidOperationException(clear);
        foreach (var marker in markers.Take(512))
        {
            // Type 3 is the only synthetic record kind currently enabled.
            // Other kinds carry additional type-specific payload fields and
            // must not be created by retagging a generic record.
            const uint type = 3;
            var command = FormattableString.Invariant($"NAVIGATION\tMARKER\tADD\t{type}\t{marker.WorldX:R}\t{marker.WorldY:R}\t{marker.WorldZ:R}");
            var response = await SendAsync(command, cancellationToken);
            if (!response.StartsWith("OK NAVIGATION", StringComparison.Ordinal)) throw new InvalidOperationException(response);
        }
    }

    public async Task<string> GetNavigationStatusAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability("NAVIGATION");
        var response = await SendAsync("NAVIGATION\tSTATUS", cancellationToken);
        if (!response.StartsWith("OK NAVIGATION", StringComparison.Ordinal)) throw new InvalidOperationException(response);
        return response;
    }

    public async Task<MinimapGeometry?> GetMinimapGeometryAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability("MINIMAP");
        return MinimapGeometry.Parse(await SendAsync("MINIMAP\tBOUNDS", cancellationToken));
    }

    public async Task<MinimapGeometry?> GetMinimapFrameAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsMinimapProjection) return null;
        return MinimapProjection.ParseFrame(await SendAsync(SupportsNativeRadar ? "MINIMAP\tFRAME2" : "MINIMAP\tFRAME", cancellationToken));
    }

    public async Task<WorldPosition> GetPlayerPositionAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability("POSITION");
        var response = await SendAsync("PLAYER\tPOSITION", cancellationToken);
        var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 5 && parts[0] == "OK" && parts[1] == "POSITION" &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
            float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return new WorldPosition(x, y, z);
        throw new InvalidOperationException(response);
    }

    public async Task<string> GetPlayerRegionPathAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability("REGION");
        var response = await SendAsync("PLAYER\tREGION", cancellationToken);
        const string prefix = "OK REGION\t";
        if (response.StartsWith(prefix, StringComparison.Ordinal) && response.Length > prefix.Length)
            return response[prefix.Length..].Replace('\\', '/');
        throw new InvalidOperationException(response);
    }

    public async Task<IReadOnlyList<string>> GetActiveQuestPathsAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability("QUESTS");
        var response = await SendAsync("QUEST\tACTIVE", cancellationToken);
        var parts = response.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts[0].StartsWith("OK ACTIVE_QUESTS ", StringComparison.Ordinal))
            throw new InvalidOperationException(response);
        return parts.Skip(1).ToArray();
    }

    public async Task<QuestNavigationState?> GetQuestNavigationStateAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsQuestTasks) return null;
        return QuestNavigationState.Parse(await SendAsync("QUEST\tTASKS", cancellationToken));
    }

    public async Task SetNativeRadarFrameAsync(ulong context, IEnumerable<NavigationOverlayPoint> points,
        CancellationToken cancellationToken = default)
    {
        EnsureCapability("RADAR_CANVAS");
        foreach (var command in NativeRadarFrame.Commands(context, points))
        {
            var response = await SendAsync(command, cancellationToken);
            if (response != "OK RADAR_CANVAS") throw new InvalidOperationException(response);
        }
    }

    public async Task ClearNativeRadarAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsNativeRadar) return;
        var response = await SendAsync("RADAR\tCANVAS\tCLEAR", cancellationToken);
        if (response != "OK RADAR_CANVAS") throw new InvalidOperationException(response);
    }

    public async Task<NativeRadarStatus> GetNativeRadarStatusAsync(CancellationToken cancellationToken = default) =>
        NativeRadarStatus.Parse(await SendAsync("RADAR\tCANVAS\tSTATUS", cancellationToken));

    public async Task<IReadOnlyList<LiveMapMarkerRecord>> GetLiveMapMarkersAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability("RADAR");
        var response = await SendAsync("RADAR\tMARKERS", cancellationToken);
        var parts = response.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts[0].StartsWith("OK RADAR_MARKERS ", StringComparison.Ordinal))
            throw new InvalidOperationException(response);
        var markers = new List<LiveMapMarkerRecord>();
        foreach (var part in parts.Skip(1))
        {
            var fields = part.Split(',', 5);
            if (fields.Length >= 4 && uint.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var type) &&
                float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                float.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                markers.Add(new LiveMapMarkerRecord(type, new WorldPosition(x, y, z),
                    fields.Length == 5 && fields[4].Length > 0 ? fields[4] : null));
        }
        return markers;
    }

    public async Task SetRadarCaptureAsync(bool activeQuests, bool pointsOfInterest = false,
        CancellationToken cancellationToken = default)
    {
        EnsureCapability("RADAR");
        var response = await SendAsync($"RADAR\tSET\t{(activeQuests ? 1 : 0)}\t{(pointsOfInterest ? 1 : 0)}", cancellationToken);
        if (!response.StartsWith("OK RADAR", StringComparison.Ordinal)) throw new InvalidOperationException(response);
    }

    public async Task SetNearPlayerMarkerAsync(float xOffset, float yOffset = 0, float zOffset = 0,
        uint markerType = 3, bool clearExisting = true, CancellationToken cancellationToken = default)
    {
        EnsureCapability("NAVIGATION");
        EnsureCapability("POSITION");
        if (markerType != 3) throw new ArgumentOutOfRangeException(nameof(markerType));
        if (clearExisting)
        {
            var clear = await SendAsync("NAVIGATION\tMARKERS\tCLEAR", cancellationToken);
            if (!clear.StartsWith("OK NAVIGATION", StringComparison.Ordinal)) throw new InvalidOperationException(clear);
        }
        var command = FormattableString.Invariant($"NAVIGATION\tMARKER\tNEAR\t{markerType}\t{xOffset:R}\t{yOffset:R}\t{zOffset:R}");
        var response = await SendAsync(command, cancellationToken);
        if (!response.StartsWith("OK NAVIGATION", StringComparison.Ordinal)) throw new InvalidOperationException(response);
    }

    private async Task<string> RunLuaSynchronousAsync(string script, CancellationToken cancellationToken) =>
        await SendAsync("LUA_SYNC\t" + script.Replace('\r', ' ').Replace('\n', ' '), cancellationToken);

    private static CharacterResources ParseResources(string response)
    {
        var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 6 && parts[0] == "OK" && parts[1] == "RESOURCES" &&
            uint.TryParse(parts[2], out var money) && uint.TryParse(parts[3], out var skill) &&
            uint.TryParse(parts[4], out var attribute) && uint.TryParse(parts[5], out var devotion))
            return new(money, skill, attribute, devotion);
        throw new InvalidOperationException(response);
    }

    private void EnsureCapability(string capability)
    {
        if (!IsConnected) throw new InvalidOperationException("Connect to Grim Dawn first.");
        if (ProtocolVersion < 2) throw new InvalidOperationException("Grim Dawn is still running the previous bridge. Restart the game, reconnect, and the updated bridge will load.");
        if (!Capabilities.Contains(capability)) throw new InvalidOperationException($"The current game build did not pass verification for the {capability.ToLowerInvariant()} capability.");
    }

    private static string ValidateToken(string token)
    {
        token = token.Trim();
        if (token.Length is < 3 or > 159 || !token.Any(char.IsLetter) || token.Any(c => !(char.IsUpper(c) || char.IsDigit(c) || c is '_' or '-' or ':' or '.')))
            throw new ArgumentException("Choose a valid token from the installed script catalog.", nameof(token));
        return token;
    }

    private async Task<bool> TryOpenPipeAsync(int processId, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (IsConnected) return true;
        await DisposePipeAsync();
        var pipe = new NamedPipeClientStream(".", $"GrimDawnCompanion.{processId}", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);
            await pipe.ConnectAsync(timeout.Token);
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { pipe.Dispose(); return false; }
        catch (IOException) { pipe.Dispose(); return false; }
    }

    private async Task<string> SendAsync(string command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _sendLock.WaitAsync(timeout.Token);
        try
        {
            var writer = _writer;
            var reader = _reader;
            if (writer is null || reader is null) throw new InvalidOperationException("The game bridge is disconnected.");
            await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
            return await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("The game bridge disconnected.");
        }
        finally { _sendLock.Release(); }
    }

    private static void Inject(Process process, string dllPath)
    {
        var processHandle = OpenProcess(0x0002 | 0x0008 | 0x0020 | 0x0400, false, (uint)process.Id);
        if (processHandle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Grim Dawn process.");
        try
        {
            var bytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            var remote = VirtualAllocEx(processHandle, 0, (nuint)bytes.Length, 0x1000 | 0x2000, 0x04);
            if (remote == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate bridge path memory.");
            try
            {
                if (!WriteProcessMemory(processHandle, remote, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not write the bridge path.");
                var kernel = GetModuleHandle("kernel32.dll");
                var loadLibrary = GetProcAddress(kernel, "LoadLibraryW");
                var thread = CreateRemoteThread(processHandle, 0, 0, loadLibrary, remote, 0, out _);
                if (thread == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not start the bridge loader.");
                try
                {
                    if (WaitForSingleObject(thread, 10_000) != 0) throw new TimeoutException("Timed out while loading the game bridge.");
                    if (!GetExitCodeThread(thread, out var result) || result == 0) throw new InvalidOperationException("Windows did not load the game bridge DLL.");
                }
                finally { CloseHandle(thread); }
            }
            finally { VirtualFreeEx(processHandle, remote, 0, 0x8000); }
        }
        finally { CloseHandle(processHandle); }
    }

    private Task DisposePipeAsync()
    {
        AbortPipe();
        return Task.CompletedTask;
    }

    private void AbortPipe()
    {
        var pipe = _pipe;
        var reader = _reader;
        var writer = _writer;
        _pipe = null;
        _reader = null;
        _writer = null;
        try { pipe?.Dispose(); } catch { }
        try { reader?.Dispose(); } catch { }
        try { writer?.Dispose(); } catch { }
    }

    private void ResetConnectionState()
    {
        ProcessId = null;
        ProtocolVersion = 0;
        Capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public void DisconnectLocally()
    {
        AbortPipe();
        ResetConnectionState();
    }

    public ValueTask DisposeAsync()
    {
        DisconnectLocally();
        return ValueTask.CompletedTask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protect);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandle(string moduleName);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)] private static partial nint GetProcAddress(nint module, string name);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint CreateRemoteThread(nint process, nint attributes, nuint stackSize, nint startAddress, nint parameter, uint flags, out uint threadId);
    [LibraryImport("kernel32.dll")] private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetExitCodeThread(nint thread, out uint exitCode);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint handle);
}
