using GrimDawnCompanion.Core;
using System.Xml.Linq;

internal static class CharacterResourceRegressionTests
{
    public static void Run(string root, Action<bool,string> require)
    {
        var profiles=new[]{(85,223,90,50),(100,244,105,55),(100,248,107,55),(100,250,109,55)};
        for(var i=0;i<profiles.Length;i++)
        {
            var (level,skill,attr,dev)=profiles[i];
            var limits=CharacterResourceLimits.Parse($"OK RESOURCE_LIMITS 10 {level} {i} 2000000000 {skill} {attr} {dev} 20 9 3 10 10 5 8 0");
            require(limits.Available(1)==skill-20 && limits.Available(2)==attr-9 && limits.Available(3)==dev-3 && limits.Available(0)==2_000_000_000,
                $"campaign {i} uses full endgame budgets less already allocated points");
        }
        foreach(var target in new[]{0,230})
        {
            var value=CharacterResourcePreparation.Parse($"OK RESOURCE_PREPARED 7 1 10 {target} 230 20 250 0054006500730074");
            require(value.Target==target && value.Name=="Test" && value.Spent==20,"set confirmation permits zero and maximum unspent balances");
        }
        foreach(var invalid in new[]{
            "OK RESOURCE_PREPARED 7 1 10 231 230 20 250 0054",
            "OK RESOURCE_PREPARED 0 1 10 0 230 20 250 0054",
            "OK RESOURCE_PREPARED 7 4 10 0 230 20 250 0054",
            "OK RESOURCE_PREPARED 7 1 10 0 231 20 250 0054",
            "OK RESOURCE_PREPARED 7 1 10 0 0 251 250 0054",
            "OK RESOURCE_PREPARED 7 0 0 2000000001 2000000001 0 2000000001 0054",
            "OK RESOURCE_PREPARED 7 1 10 -1 230 20 250 0054",
            "OK RESOURCE_PREPARED 7 1 10 4294967296 230 20 250 0054",
            "OK RESOURCE_PREPARED 7 1 10 0 230 20 250 000A"})
        {
            try {CharacterResourcePreparation.Parse(invalid);}
            catch(InvalidDataException) {continue;}
            throw new InvalidOperationException("Invalid resource preparation accepted: "+invalid);
        }
        require(true,"set confirmation rejects over-cap, stale, negative, overflowing and malformed values");
        foreach(var invalid in new[]{
            "OK RESOURCE_LIMITS 10 200 3 2000000000 250 109 55 20 9 3 10 10 5 8 0",
            "OK RESOURCE_LIMITS 10 100 4 2000000000 250 109 55 20 9 3 10 10 5 8 0",
            "OK RESOURCE_LIMITS 10 100 3 2000000000 251 109 55 20 9 3 10 10 5 8 0",
            "OK RESOURCE_LIMITS 10 1001 3 2000000000 310 208 100 20 9 3 10 10 5 8 1",
            "OK RESOURCE_LIMITS 10 100 3 2000000000 1000001 208 100 20 9 3 10 10 5 8 1"})
        {
            try {CharacterResourceLimits.Parse(invalid);}
            catch(InvalidDataException) {continue;}
            throw new InvalidOperationException("Invalid resource profile accepted.");
        }
        require(true,"unknown campaign profiles fail closed");
        var mod=CharacterResourceLimits.Parse("OK RESOURCE_LIMITS 10 100 3 2000000000 310 208 100 20 9 3 10 10 5 8 1");
        require(mod.UsesModRules && mod.Available(1)==290 && mod.Available(2)==199 && mod.Available(3)==97,"active mod budgets are accepted and adjusted for allocations");
        require(CharacterResourcePreparation.Parse("OK RESOURCE_PREPARED 7 3 5 97 97 3 100 0054").Maximum==97,"mod devotion confirmation is not clamped to vanilla 55");
        var mismatch=CharacterResourceLimits.Parse("OK RESOURCE_LIMITS 10 100 3 2000000000 250 109 55 20 9 3 10 10 5 7 0");
        require(!mismatch.DevotionAccountingValid && mismatch.Available(1)==230,"inconsistent devotion totals remain inspectable without blocking unrelated resource caps");
        var client=new GameBridgeClient();
        try
        {
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(client,new HashSet<string>{"RESOURCE_SET","CHARACTER_LEVEL","LOADED_PROGRESSION"});
            foreach(var version in new[]{35,36})
            {
                typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(client,version);
                require(client.SupportsResourceSet==(version==36) && client.SupportsCharacterLevel==(version==36),"older bridges cannot expose mod-aware resource or level controls");
            }
        }
        finally {client.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        var doc=XDocument.Load(Path.Combine(root,"src/GrimDawnCompanion.App/MainWindow.xaml"));
        require(doc.Descendants().Count(e=>(string?)e.Attribute("Click")=="SetCharacterResource")==4 &&
            !doc.Descendants().Any(e=>(string?)e.Attribute("Click")=="AddCharacterResource"),"all four resource controls use Set instead of Add");
        require(doc.Descendants().Any(e=>(string?)e.Attribute("Content")=="Level Character"),"level action is titled Level Character");
        var native=File.ReadAllText(Path.Combine(root,"src/GrimDawnBridge/Bridge.cpp"));
        require(!native.Contains("WorkKind::ResourceAdd") && native.Contains("RESOURCE_USE_CAPPED_SET_WORKFLOW"),"old uncapped add path is removed from the bridge");
    }
}
