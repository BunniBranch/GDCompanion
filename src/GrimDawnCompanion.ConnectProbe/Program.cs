using GrimDawnCompanion.Core;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

var gameArgument = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;
var bridgeArgument = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
var game = GameLocator.Locate(gameArgument)
    ?? throw new InvalidOperationException($"Grim Dawn {(Environment.Is64BitProcess ? "x64" : "x86")} installation was not found.");
var bridge = bridgeArgument is not null
    ? Path.GetFullPath(bridgeArgument)
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "GrimDawnBridge.dll"));

Console.WriteLine($"Game:   {game.ExecutablePath}");
Console.WriteLine($"Bridge: {bridge} (last written {File.GetLastWriteTime(bridge):u})");

if (args.Contains("--inspect-navigation-hooks", StringComparer.OrdinalIgnoreCase))
{
    var process = Process.GetProcessesByName("Grim Dawn").Single();
    var gameModule = process.Modules.Cast<ProcessModule>().Single(module =>
        string.Equals(module.ModuleName, "Game.dll", StringComparison.OrdinalIgnoreCase));
    var exports = new PeExportReader(gameModule.FileName);
    var targets = new[]
    {
        "?GetDetailMapData@GameEngine@GAME@@QEAAXAEAV?$vector@UMinimapGameNugget@GAME@@@mem@@AEBVWorldFrustum@2@@Z",
        "?AppendDetailMapData@AreaOfInterest@GAME@@UEAAXAEAV?$vector@UMinimapGameNugget@GAME@@@mem@@@Z",
        "?IsMarkerUIDKnown@Player@GAME@@QEBA_NAEBVUniqueId@2@@Z",
        "?IsTracked@Quest2@GAME@@QEBA_NXZ"
    };
    var handle = NativeProbe.OpenProcess(0x0010 | 0x0400, false, (uint)process.Id);
    if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    try
    {
        foreach (var target in targets)
        {
            var symbol = exports.Find(target) ?? throw new InvalidOperationException($"Missing export: {target}");
            var bytes = new byte[16];
            var address = gameModule.BaseAddress + checked((int)symbol.Rva);
            if (!NativeProbe.ReadProcessMemory(handle, address, bytes, (nuint)bytes.Length, out var read) || read != (nuint)bytes.Length)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Console.WriteLine($"0x{address:X} {target}: {Convert.ToHexString(bytes)}");
        }
    }
    finally { NativeProbe.CloseHandle(handle); }
    return 0;
}

await using var client = new GameBridgeClient();
try
{
    var response = await client.ConnectAsync(game, bridge);
    Console.WriteLine($"SUCCESS: PID {client.ProcessId}, {response}");
    if (args.Contains("--lua-smoke", StringComparer.OrdinalIgnoreCase))
    {
        var send = typeof(GameBridgeClient).GetMethod("SendAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var luaResponse = await (Task<string>)send.Invoke(client, ["LUA_SYNC\tlocal gdcompanion_x86_smoke=1", CancellationToken.None])!;
        Console.WriteLine("LUA: " + luaResponse);
        var unloaded = await client.UnloadAsync();
        Console.WriteLine(unloaded ? "SUCCESS: bridge accepted unload and disconnected" : "NOTICE: bridge does not support self-unload");
        return luaResponse == "OK LUA" && unloaded ? 0 : 2;
    }
    if (args.Contains("--minimap-dump", StringComparer.OrdinalIgnoreCase))
    {
        var send = typeof(GameBridgeClient).GetMethod("SendAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (var index = 0; index < 5; index++)
        {
            await Task.Delay(1000);
            Console.WriteLine(await (Task<string>)send.Invoke(client, ["MINIMAP\tBOUNDS", CancellationToken.None])!);
        }
        _ = await client.UnloadAsync();
        return 0;
    }
    if (args.Contains("--map-probe", StringComparer.OrdinalIgnoreCase))
    {
        if (!client.Capabilities.Contains("MAP_PROBE_V2"))
        {
            Console.WriteLine("NOTICE: the running game does not expose MAP_PROBE_V2.");
            Console.WriteLine(client.Capabilities.Contains("MAP_PROBE")
                ? "An older probe build is loaded."
                : "Either the game is using an older bridge DLL, or the map-render hooks did not enable.");
            Console.WriteLine($"Bridge file: {bridge} (last written {File.GetLastWriteTime(bridge):u})");
            Console.WriteLine($"Handshake: {response}");
            var released = await client.UnloadAsync();
            Console.WriteLine(released
                ? "The loaded bridge was unloaded; rerun --map-probe to load the current build."
                : "The bridge did not confirm unload; fully exit Grim Dawn, restart it, load a character, and rerun.");
            return 3;
        }
        var send = typeof(GameBridgeClient).GetMethod("SendAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        try
        {
            var position = await client.GetPlayerPositionAsync();
            var armed = await (Task<string>)send.Invoke(client, [
                string.Create(CultureInfo.InvariantCulture, $"MAP\tPROBE\tSTART\t{position.X:R}\t{position.Z:R}"),
                CancellationToken.None])!;
            Console.WriteLine("PROBE: " + armed);
            Console.WriteLine("A magenta target is drawn at your character's world position on the game's map surface.");
            Console.WriteLine("PHASE 1 (6 s): keep the full map closed and watch the minimap for the target.");
            for (var index = 0; index < 6; index++)
            {
                await Task.Delay(1_000);
                Console.WriteLine(await (Task<string>)send.Invoke(client, ["MAP\tPROBE\tSTATUS", CancellationToken.None])!);
            }
            Console.WriteLine("PHASE 2 (12 s): press M now and hold the full map open for the rest of the run.");
            for (var index = 0; index < 12; index++)
            {
                await Task.Delay(1_000);
                Console.WriteLine(await (Task<string>)send.Invoke(client, ["MAP\tPROBE\tSTATUS", CancellationToken.None])!);
            }
            Console.WriteLine(await (Task<string>)send.Invoke(client, ["MAP\tPROBE\tSTOP", CancellationToken.None])!);
        }
        finally
        {
            var released = await client.UnloadAsync(CancellationToken.None);
            Console.WriteLine(released
                ? "SUCCESS: bridge unloaded and disconnected"
                : "NOTICE: bridge did not confirm unload");
        }
        return 0;
    }
    if (args.Contains("--map-lane-test", StringComparer.OrdinalIgnoreCase))
    {
        if (!client.Capabilities.Contains("MAP_CANVAS"))
        {
            Console.WriteLine("NOTICE: the running game does not expose MAP_CANVAS.");
            Console.WriteLine($"Handshake: {response}");
            var released = await client.UnloadAsync();
            Console.WriteLine(released
                ? "The loaded bridge was unloaded; rerun --map-lane-test to load the current build."
                : "The bridge did not confirm unload; fully exit Grim Dawn, restart it, and rerun.");
            return 3;
        }
        var send = typeof(GameBridgeClient).GetMethod("SendAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        async Task<string> Raw(string command) =>
            await (Task<string>)send.Invoke(client, [command, CancellationToken.None])!;
        try
        {
            await client.SetRadarCaptureAsync(true, true);
            var position = await client.GetPlayerPositionAsync();
            Console.WriteLine($"PLAYER {position.X:R},{position.Z:R}; open the full map (M) and hold it open.");
            for (var index = 0; index < 20; index++)
            {
                await Task.Delay(1_000);
                var frame = await Raw("MAP\tFRAME");
                var fields = frame.Split(' ');
                if (fields.Length == 4 && fields[2] == "OPEN" && ulong.TryParse(fields[3], out var context) && context != 0)
                {
                    var x = position.X.ToString("R", CultureInfo.InvariantCulture);
                    var z = position.Z.ToString("R", CultureInfo.InvariantCulture);
                    var begin = await Raw($"MAP\tCANVAS\tBEGIN\t{context}\t1");
                    var add = await Raw($"MAP\tCANVAS\tADD\t{x},{z},3,4D41504C414E45");
                    var commit = await Raw("MAP\tCANVAS\tCOMMIT");
                    var lane = await Raw("MAP\tCANVAS\tSTATUS");
                    var diag = await Raw("MAP\tCANVAS\tDIAG");
                    Console.WriteLine($"{frame} | {begin} {add} {commit} | {lane} | {diag}");
                }
                else Console.WriteLine(frame);
            }
            Console.WriteLine("RADAR: " + await Raw("RADAR\tCANVAS\tSTATUS"));
            Console.WriteLine("MAP CLEAR: " + await Raw("MAP\tCANVAS\tCLEAR"));
        }
        finally
        {
            var released = await client.UnloadAsync(CancellationToken.None);
            Console.WriteLine(released ? "SUCCESS: bridge unloaded and disconnected" : "NOTICE: bridge did not confirm unload");
        }
        return 0;
    }
    if (args.Contains("--status", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(await client.GetNavigationStatusAsync());
        var activeQuests = await client.GetActiveQuestPathsAsync();
        var markers = await client.GetLiveMapMarkersAsync();
        foreach (var quest in activeQuests) Console.WriteLine($"QUEST {quest}");
        foreach (var marker in markers.OrderBy(marker => marker.Type).ThenBy(marker => marker.Position.X))
            Console.WriteLine($"TYPE {marker.Type}: {marker.Position.X:R},{marker.Position.Y:R},{marker.Position.Z:R} LABEL={marker.Label ?? "<none>"}");
        return 0;
    }
    var navigationTestSeconds = args.Contains("--navigation-test-long", StringComparer.OrdinalIgnoreCase) ? 20 :
        args.Contains("--navigation-test", StringComparer.OrdinalIgnoreCase) ? 10 : 0;
    if (args.Contains("--radar-dump", StringComparer.OrdinalIgnoreCase))
    {
        await client.SetNavigationAsync(true, true);
        await client.SetRadarCaptureAsync(true, true);
        Console.WriteLine("Radar capture enabled; sampling the live minimap for three seconds.");
        await Task.Delay(3_000);
        var position = await client.GetPlayerPositionAsync();
        var activeQuests = await client.GetActiveQuestPathsAsync();
        var markers = await client.GetLiveMapMarkersAsync();
        Console.WriteLine($"PLAYER {position.X:R},{position.Y:R},{position.Z:R}");
        foreach (var quest in activeQuests) Console.WriteLine($"QUEST {quest}");
        foreach (var marker in markers.OrderBy(marker => marker.Type).ThenBy(marker => marker.Position.X))
            Console.WriteLine($"TYPE {marker.Type}: {marker.Position.X:R},{marker.Position.Y:R},{marker.Position.Z:R} LABEL={marker.Label ?? "<none>"}");
        Console.WriteLine(await client.GetNavigationStatusAsync());
        await client.SetRadarCaptureAsync(false, false);
        _ = await client.UnloadAsync();
        return 0;
    }
    if (args.Contains("--near-player-marker-test", StringComparer.OrdinalIgnoreCase))
    {
        var position = await client.GetPlayerPositionAsync();
        await client.SetNearPlayerMarkerAsync(4f);
        await client.SetNavigationAsync(false, true);
        Console.WriteLine($"Near-player POI marker enabled four local units south of X={position.X:R}, Y={position.Y:R}, Z={position.Z:R}. Open the full map for twenty seconds.");
        for (var index = 0; index < 20; index++)
        {
            await Task.Delay(1_000);
            Console.WriteLine(await client.GetNavigationStatusAsync());
        }
        await client.ClearNavigationAsync();
        return 0;
    }
    if (args.Contains("--world-marker-test", StringComparer.OrdinalIgnoreCase))
    {
        var catalog = await new WorldMarkerCatalogService().BuildAsync(game);
        var shrines = catalog.Markers.Where(marker => marker.Category == "Devotion Shrine").Take(12).ToArray();
        await client.ReplaceWorldMarkersAsync(shrines, useNativeKinds: false);
        await client.SetNavigationAsync(false, true);
        Console.WriteLine($"World marker test enabled with {shrines.Length} indexed shrine coordinates rendered as riftgate icons. Open the full map for twenty seconds.");
        for (var index = 0; index < 20; index++)
        {
            await Task.Delay(1_000);
            Console.WriteLine(await client.GetNavigationStatusAsync());
        }
        await client.ClearNavigationAsync();
        return 0;
    }
    if (navigationTestSeconds > 0)
    {
        await client.SetNavigationAsync(true, true);
        Console.WriteLine($"Navigation enabled. Open the map and minimap during the next {navigationTestSeconds} seconds.");
        for (var index = 0; index < navigationTestSeconds; index++)
        {
            await Task.Delay(1_000);
            Console.WriteLine(await client.GetNavigationStatusAsync());
        }
        await client.ClearNavigationAsync();
        return 0;
    }
    if (args.Contains("--unload", StringComparer.OrdinalIgnoreCase))
    {
        var unloaded = await client.UnloadAsync();
        Console.WriteLine(unloaded ? "SUCCESS: bridge accepted unload and disconnected" : "NOTICE: bridge does not support self-unload");
        return unloaded ? 0 : 2;
    }
    return 0;
}

catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

internal static class NativeProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
}
