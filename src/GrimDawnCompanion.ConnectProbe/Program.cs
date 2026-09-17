using GrimDawnCompanion.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

var game = GameLocator.Locate(args.Length > 0 ? args[0] : null)
    ?? throw new InvalidOperationException("Grim Dawn x64 installation was not found.");
var bridge = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "GrimDawnBridge.dll"));

Console.WriteLine($"Game:   {game.ExecutablePath}");
Console.WriteLine($"Bridge: {bridge}");

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
