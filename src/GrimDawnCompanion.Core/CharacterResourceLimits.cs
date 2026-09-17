using System.Globalization;
using System.Text;

namespace GrimDawnCompanion.Core;

public sealed record CharacterResourceLimits(uint Level, uint LevelCap, uint Expansion, uint[] Caps, uint[] Spent, uint[] Unspent, uint DevotionEarned, bool UsesModRules)
{
    public const uint BypassPointBudget = 10_000;
    public bool DevotionAccountingValid => (ulong)Spent[3]+Unspent[3]==DevotionEarned;
    public uint EffectiveCap(int kind, bool bypass=false) => bypass && kind!=0 ? Math.Max(Caps[kind],BypassPointBudget) : Caps[kind];
    public uint Available(int kind, bool bypass=false) => Spent[kind] <= EffectiveCap(kind,bypass) ? EffectiveCap(kind,bypass) - Spent[kind] : 0;
    public static CharacterResourceLimits Parse(string response)
    {
        var p=response.Split(' ');
        if (p.Length!=17 || p[0]!="OK" || p[1]!="RESOURCE_LIMITS") throw new InvalidDataException(response);
        var v=p.Skip(2).Select(ReadNumber).ToArray();
        var skill=new uint[]{223,244,248,250};var attr=new uint[]{90,105,107,109};
        if (v[2]>3 || v[0]<1 || v[0]>v[1] || v[1]>CharacterLevelPreparation.HardLimit || v[14]>1 ||
            v[3]!=2_000_000_000 || v[4]>1_000_000 || v[5]>1_000_000 || v[6]>1_000_000 ||
            (v[14]==0 && (v[1]!=(v[2]==0 ? 85 : 100) || v[4]!=skill[v[2]] || v[5]!=attr[v[2]] || v[6]!=(v[2]==0 ? 50 : 55))))
            throw new InvalidDataException("Unsupported campaign resource limits.");
        return new(v[0],v[1],v[2],v[3..7],[0,v[7],v[8],v[9]],[0,v[10],v[11],v[12]],v[13],v[14]==1);
    }
    internal static uint ReadNumber(string text) => uint.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value)
        ? value : throw new InvalidDataException("Invalid resource value.");
}

public sealed record CharacterResourcePreparation(ulong Token, uint Kind, uint Current, uint Target, uint Maximum, uint Spent, uint Cap, string Name)
{
    public static CharacterResourcePreparation Parse(string response)
    {
        var p=response.Split(' ');
        if (p.Length!=10 || p[0]!="OK" || p[1]!="RESOURCE_PREPARED" ||
            !ulong.TryParse(p[2],NumberStyles.None,CultureInfo.InvariantCulture,out var token) || token==0) throw new InvalidDataException(response);
        var v=p.Skip(3).Take(6).Select(CharacterResourceLimits.ReadNumber).ToArray();
        if (v[0]>3 || v[4]>v[5] || v[3]!=v[5]-v[4] || v[2]>v[3] ||
            v[5]>(v[0]==0 ? 2_000_000_000u : 1_000_000u) ||
            p[9].Length is 0 or >512 || p[9].Length%4!=0) throw new InvalidDataException(response);
        var name=new StringBuilder();
        for(var i=0;i<p[9].Length;i+=4)
        {
            if(!ushort.TryParse(p[9].AsSpan(i,4),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var c) || char.IsControl((char)c))
                throw new InvalidDataException(response);
            name.Append((char)c);
        }
        return new(token,v[0],v[1],v[2],v[3],v[4],v[5],name.ToString());
    }
}

public sealed partial class GameBridgeClient
{
    public bool SupportsResourceSet => ProtocolVersion>=36 && Capabilities.Contains("RESOURCE_SET") && Capabilities.Contains("LOADED_PROGRESSION");
    public bool SupportsResourceCapBypass => SupportsResourceSet && ProtocolVersion>=38 && Capabilities.Contains("RESOURCE_CAP_BYPASS");
    public async Task<CharacterResourceLimits> GetResourceLimitsAsync(CancellationToken cancellationToken=default)
    {
        if(!SupportsResourceSet) throw new InvalidOperationException("Reconnect with the updated Companion to use capped resource controls.");
        return CharacterResourceLimits.Parse(await SendAsync("RESOURCE\tLIMITS",cancellationToken));
    }
    public async Task<CharacterResourcePreparation> PrepareResourceAsync(string resource,uint target,CancellationToken cancellationToken=default,bool bypass=false)
    {
        if(!SupportsResourceSet) throw new InvalidOperationException("Reconnect with the updated Companion to set resources safely.");
        if(bypass && !SupportsResourceCapBypass) throw new InvalidOperationException("Reconnect with the updated Companion to use the natural cap bypass.");
        var kind=resource switch {"money"=>0u,"skill"=>1u,"attribute"=>2u,"devotion"=>3u,_=>throw new ArgumentOutOfRangeException(nameof(resource))};
        var limits=await GetResourceLimitsAsync(cancellationToken);
        if(kind==3 && !limits.DevotionAccountingValid) throw new InvalidOperationException($"Devotion changes are blocked: the game reports {limits.Spent[3]} allocated + {limits.Unspent[3]} unspent, but {limits.DevotionEarned} earned. Open the Skills/Devotion page and refresh. If the mismatch remains, inspect the character before changing devotion points.");
        if(target>limits.Available((int)kind,bypass)) throw new ArgumentOutOfRangeException(nameof(target),$"Maximum unspent balance: {limits.Available((int)kind,bypass):N0}. Already allocated: {limits.Spent[kind]:N0}; permitted total: {limits.EffectiveCap((int)kind,bypass):N0}.");
        var op=bypass ? "PREPARE_BYPASS" : "PREPARE";
        var prepared=CharacterResourcePreparation.Parse(await SendAsync($"RESOURCE\t{op}\t{resource}\t{target}",cancellationToken));
        if(prepared.Kind!=kind || prepared.Target!=target || prepared.Cap!=limits.EffectiveCap((int)kind,bypass) || prepared.Spent!=limits.Spent[kind]) throw new InvalidDataException("Resource rules or allocations changed. Refresh and try again.");
        return prepared;
    }
    public async Task<CharacterResources> CommitResourceAsync(CharacterResourcePreparation prepared,CancellationToken cancellationToken=default)
    {
        if(!SupportsResourceSet) throw new InvalidOperationException("Capped resource changes are unavailable.");
        var result=ParseResources(await SendAsync($"RESOURCE\tCOMMIT\t{prepared.Token}",cancellationToken));
        var actual=prepared.Kind switch {0=>result.Money,1=>result.SkillPoints,2=>result.AttributePoints,3=>result.DevotionPoints,_=>uint.MaxValue};
        if(actual!=prepared.Target) throw new InvalidDataException("Resource result was not verified. Do not retry automatically.");
        return result;
    }
}
