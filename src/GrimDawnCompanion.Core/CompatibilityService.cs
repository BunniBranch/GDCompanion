using System.Text.Json;

namespace GrimDawnCompanion.Core;

public sealed class CompatibilityService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _profilePath;

    private static readonly SymbolDefinition[] Definitions =
    [
        new("Engine.dll", "?Update@LuaManager@GAME@@QEAAXH@Z", "LuaManager::Update(int)", true),
        new("Game.dll", "?s_instance@?$Singleton@VQuest2Repository@GAME@@@GAME@@0PEAVQuest2Repository@2@EA", "Live tracked quest repository", false, false),
        new("Game.dll", "?IsTracked@Quest2@GAME@@QEBA_NXZ", "Quest tracking state", false),
        new("Game.dll", "?GetExperiencePoints@Character@GAME@@QEBA?BIXZ", "Character level XP inspection", false),
        new("Game.dll", "?GetNextLevelExperience@Character@GAME@@QEBA?BIXZ", "Next character level XP", false),
        new("Game.dll", "?GetTotalCharAttribute@Character@GAME@@QEBAMW4CharAttributeType@2@@Z", "Character XP bonus safety check", false),
        new("Game.dll", "?IncrementCharLevel@Character@GAME@@QEAAXXZ", "Native character level-up", false),
        new("Engine.dll", "?RunCode@LuaManager@GAME@@QEAA_NPEBD@Z", "LuaManager::RunCode(char const*)", true),
        new("Game.dll", "?CreateItem@Item@GAME@@SAPEAV12@AEBUItemReplicaInfo@2@@Z", "Affix item creation", false),
        new("Game.dll", "?GetItemReplicaInfo@Item@GAME@@UEBAXAEAUItemReplicaInfo@2@@Z", "Affix item result verification", false),
        new("Game.dll", "?GiveItemToCharacter@Player@GAME@@UEAAXPEAVItem@2@_N1@Z", "Affix item delivery", false),
        new("Engine.dll", "?GetValue@LoadTableBinary@GAME@@UEBAPEBDPEBD0@Z", "Affix loaded-data compatibility", false),
        new("Engine.dll", "?LoadTableFile@ObjectManager@GAME@@QEAAPEBVLoadTable@2@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z", "Affix active database record loading", false),
        new("Engine.dll", "?DestroyObjectEx@ObjectManager@GAME@@QEAAXPEAVObject@2@PEBDH@Z", "Affix unowned item cleanup", false),
        new("Engine.dll", "?gEngine@GAME@@3PEAVEngine@1@EA", "Minimap engine instance", false, false),
        new("Engine.dll", "?GetGraphicsEngine@Engine@GAME@@QEBAPEAVGraphicsEngine@2@XZ", "Minimap graphics engine", false),
        new("Engine.dll", "?GetWidth@GraphicsEngine@GAME@@QEBAHXZ", "Minimap render width", false),
        new("Engine.dll", "?RenderTriFan@GraphicsCanvas@GAME@@QEAAXAEBV?$vector@VVec2@GAME@@@mem@@AEBVColor@2@@Z", "In-game radar glyph rendering", false),
        new("Engine.dll", "?RenderRect@GraphicsCanvas@GAME@@QEAAXAEBVRect@2@AEBVColor@2@@Z", "In-game radar tooltip background", false),
        new("Engine.dll", "?RenderColoredText2d@GraphicsCanvas@GAME@@QEAAXHHAEBV?$basic_string@GU?$char_traits@G@std@@V?$allocator@G@2@@std@@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@4@AEBVColor@2@W4FontLayout@2@@Z", "In-game radar tooltip text", false),
        new("Engine.dll", "?GetHeight@GraphicsEngine@GAME@@QEBAHXZ", "Minimap render height", false),
        new("Engine.dll", "?GetFileName@Resource@GAME@@QEBAPEBDXZ", "Minimap texture identity", false),
        new("Engine.dll", "?GetTexture@GraphicsTexture@GAME@@QEBAPEBVRenderTexture@2@XZ", "Minimap texture", false),
        new("Engine.dll", "?GetTexture@GraphicsTexture@GAME@@QEBAPEBVRenderTexture@2@H@Z", "Minimap frame texture", false),
        new("Engine.dll", "?RenderShadedRect@GraphicsCanvas@GAME@@QEAAXAEBVRect@2@0PEBVRenderTexture@2@PEBVGraphicsShader2@2@AEBVName@2@AEBVColor@2@M@Z", "Minimap render bounds", false, true,
            bytes => bytes.AsSpan(26, 4).SequenceEqual(new byte[] { 0xf3, 0x0f, 0x10, 0x91 }) &&
                     bytes.AsSpan(37, 4).SequenceEqual(new byte[] { 0xf3, 0x0f, 0x10, 0x89 })),
        new("Engine.dll", "?GetCoords@Entity@GAME@@QEBA?AVWorldCoords@2@XZ", "Entity::GetCoords()", false),
        new("Engine.dll", "?GetRegionContainingXZ@World@GAME@@QEBAPEAVRegion@2@PEAV32@MM@Z", "World::GetRegionContainingXZ()", false),
        new("Engine.dll", "?SetFromWorldPosition@WorldVec3@GAME@@QEAA_NAEBVVec3@2@PEAVRegion@2@@Z", "WorldVec3::SetFromWorldPosition()", false),
        new("Engine.dll", "?GetLoadFileName@Region@GAME@@QEBA?AV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ", "Region::GetLoadFileName()", false),
        new("Engine.dll", "?GetFileName@World@GAME@@QEBAPEBDXZ", "Bookmark world identity", false),
        new("Engine.dll", "?GetZoneRecord@Region@GAME@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ", "Bookmark zone record", false),
        new("Engine.dll", "?Get@ZoneManager@GAME@@SAPEAV12@XZ", "Bookmark zone manager", false),
        new("Engine.dll", "?GetZoneData@ZoneManager@GAME@@QEBAPEBUZoneData@12@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z", "Bookmark zone label", false),
        new("Engine.dll", "?Instance@LocalizationManager@GAME@@SAAEAV12@XZ", "Bookmark localization manager", false),
        new("Engine.dll", "?LocalizeWithoutParams@LocalizationManager@GAME@@QEAAPEBGPEBD@Z", "Bookmark localized area name", false),
        new("Engine.dll", "?IsLevelLoaded@Region@GAME@@QEBA_NXZ", "Bookmark destination loaded", false),
        new("Engine.dll", "?IsLoadingFinished@Region@GAME@@QEBA_NXZ", "Bookmark destination ready", false),
        new("Engine.dll", "?IsUnderground@Region@GAME@@QEBA_NXZ", "Bookmark outdoor guard", false),
        new("Engine.dll", "?GetMode@GameInfo@GAME@@QEBAIXZ", "Bookmark campaign mode", false),
        new("Engine.dll", "?GetModName@GameInfo@GAME@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ", "Bookmark custom-game guard", false),
        new("Game.dll", "?IsHardcore@Player@GAME@@QEBA_NXZ", "Bookmark hardcore identity", false),
        new("Game.dll", "?GetGameDifficulty@GameEngine@GAME@@QEBA?AW4GameDifficulty@2@XZ", "Bookmark difficulty identity", false),
        new("Game.dll", "?IsTeleporting@Character@GAME@@QEBA_NXZ", "Bookmark travel guard", false),
        new("Game.dll", "?TeleportToLocation@Character@GAME@@UEAAXAEBVWorldCoords@2@@Z", "Bookmark guarded return", false),
        new("Game.dll", "?InitiatePlayerTeleport@GameEngine@GAME@@QEAAXHHHW4TeleportEffect@2@_N@Z", "Bookmark game-managed destination loading", false),
        new("Engine.dll", "?GetRegionContainingPoint@World@GAME@@QEBAPEAVRegion@2@AEBVIntVec3@2@@Z", "Bookmark loading destination validation", false),
        new("Game.dll", "?Get@ActivityManager@GAME@@SAPEAV12@XZ", "Bookmark travel activity manager", false),
        new("Game.dll", "?IsAlreadyTeleporting@ActivityManager@GAME@@QEBA_NXZ", "Bookmark pending travel guard", false),
        new("Game.dll", "?gGameEngine@GAME@@3PEAVGameEngine@1@EA", "GameEngine global instance", false, false),
        new("Game.dll", "?GetMainPlayer@GameEngine@GAME@@QEBAPEAVPlayer@2@XZ", "GameEngine::GetMainPlayer()", false),
        new("Game.dll", "?GetCurrentMoney@Character@GAME@@QEBA?BIXZ", "Character::GetCurrentMoney()", false),
        new("Game.dll", "?GetSkillPoints@Character@GAME@@QEBA?BIXZ", "Character::GetSkillPoints()", false),
        new("Game.dll", "?GetModifierPoints@Character@GAME@@QEBA?BIXZ", "Character::GetModifierPoints()", false),
        new("Game.dll", "?GetDevotionPoints@Character@GAME@@QEBA?BIXZ", "Character::GetDevotionPoints()", false),
        new("Game.dll", "?AddMoney@Character@GAME@@QEAAXI@Z", "Character::AddMoney(uint)", false),
        new("Game.dll", "?AddSkillPoints@Character@GAME@@QEAAXI@Z", "Character::AddSkillPoints(uint)", false),
        new("Game.dll", "?AddModifierPoints@Character@GAME@@QEAAXI@Z", "Character::AddModifierPoints(uint)", false),
        new("Game.dll", "?AddDevotionPoints@Character@GAME@@QEAAXI@Z", "Character::AddDevotionPoints(uint)", false),
        new("Game.dll", "?HasToken@Player@GAME@@QEAA_NAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z", "Player::HasToken(string)", false),
        new("Game.dll", "?GetDetailMapData@GameEngine@GAME@@QEAAXAEAV?$vector@UMinimapGameNugget@GAME@@@mem@@AEBVWorldFrustum@2@@Z", "GameEngine::GetDetailMapData()", false),
        new("Game.dll", "?AppendDetailMapData@AreaOfInterest@GAME@@UEAAXAEAV?$vector@UMinimapGameNugget@GAME@@@mem@@@Z", "AreaOfInterest::AppendDetailMapData()", false, true, HasAreaNavigationPatterns),
        new("Game.dll", "?IsMarkerUIDKnown@Player@GAME@@QEBA_NAEBVUniqueId@2@@Z", "Player::IsMarkerUIDKnown()", false),
        new("Game.dll", "?GetQuests@Quest2Repository@GAME@@QEAAXAEAV?$vector@PEAVQuest2@GAME@@@mem@@W4Filter@12@@Z", "Quest2Repository::GetQuests()", false),
        new("Game.dll", "?GetFileName@Quest2@GAME@@QEBAAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@XZ", "Quest2::GetFileName()", false),
        new("Game.dll", "?GetNumTasks@Quest2@GAME@@QEBAIXZ", "Quest2::GetNumTasks()", false),
        new("Game.dll", "?GetTaskByIndex@Quest2@GAME@@QEBAPEAVQuest2Task@2@H@Z", "Quest2::GetTaskByIndex(int)", false),
        new("Game.dll", "?GetUid@Quest2Task@GAME@@QEBAIXZ", "Quest2Task::GetUid()", false),
        new("Game.dll", "?InProgress@Quest2Task@GAME@@QEBA_N_N@Z", "Quest2Task::InProgress(bool)", false),
        new("Game.dll", "?IsOfInterest@AscendantAltar@GAME@@UEBA_NXZ", "AscendantAltar::IsOfInterest()", false),
        new("Game.dll", "?IsOfInterest@DynamicTeleporter@GAME@@UEBA_NXZ", "DynamicTeleporter::IsOfInterest()", false),
        new("Game.dll", "?IsOfInterest@FixedDoor@GAME@@UEBA_NXZ", "FixedDoor::IsOfInterest()", false),
        new("Game.dll", "?UpdateSelf@FixedDoor@GAME@@UEAAXH@Z", "FixedDoor::UpdateSelf(int)", false),
        new("Game.dll", "?GetGameDescription@FixedDoor@GAME@@UEBA?AV?$basic_string@GU?$char_traits@G@std@@V?$allocator@G@2@@std@@_N0@Z", "FixedDoor::GetGameDescription()", false),
        new("Game.dll", "?IsOfInterest@FixedItemContainer@GAME@@UEBA_NXZ", "FixedItemContainer::IsOfInterest()", false),
        new("Game.dll", "?IsOfInterest@FixedItemShrine@GAME@@UEBA_NXZ", "FixedItemShrine::IsOfInterest()", false),
        new("Game.dll", "?IsOfInterest@MonsterShrine@GAME@@UEBA_NXZ", "MonsterShrine::IsOfInterest()", false),
        new("Game.dll", "?IsOfInterest@StaticShrine@GAME@@UEBA_NXZ", "StaticShrine::IsOfInterest()", false),
        new("Game.dll", "?IsOfInterest@StaticTeleporter@GAME@@UEBA_NXZ", "StaticTeleporter::IsOfInterest()", false),
        new("Game.dll", "?AppendDetailMapData@StaticTeleporter@GAME@@UEAAXAEAV?$vector@UMinimapGameNugget@GAME@@@mem@@@Z", "StaticTeleporter::AppendDetailMapData()", false, true, HasTeleporterNuggetConstruction),
        new("Game.dll", "?SetInvincible@Character@GAME@@QEAAX_N@Z", "Character::SetInvincible(bool)", false),
        new("Game.dll", "?IsInvincible@Player@GAME@@UEBA_NXZ", "Player::IsInvincible()", false),
        new("Game.dll", "?GetRunSpeed@Character@GAME@@QEAA?BM_N@Z", "Character::GetRunSpeed(bool)", false),
        new("Game.dll", "?ForceSpeedUpdate@Character@GAME@@QEAAXXZ", "Character::ForceSpeedUpdate()", false),
        new("Game.dll", "?SubtractMana@Character@GAME@@QEAAXM@Z", "Character::SubtractMana(float)", false),
        new("Game.dll", "?SetCurrentMana@Character@GAME@@QEAAXM@Z", "Character::SetCurrentMana(float)", false),
        new("Game.dll", "?GetManaLimit@Character@GAME@@QEBA?BMXZ", "Character::GetManaLimit()", false),
        new("Game.dll", "?GetReserveMana@Character@GAME@@QEBA?BMXZ", "Character::GetReserveMana()", false),
        new("Game.dll", "?GetCurrentMana@Character@GAME@@QEBA?BMXZ", "Character::GetCurrentMana()", false),
        new("Game.dll", "?GetManager@Skill@GAME@@QEAAPEAVSkillManagerBase@2@XZ", "Skill::GetManager()", false),
        new("Game.dll", "?StartCooldown@Skill@GAME@@QEAAX_N@Z", "Skill::StartCooldown(bool)", false),
        new("Game.dll", "?EndCooldown@Skill@GAME@@UEAAXH@Z", "Skill::EndCooldown(int)", false),
        new("Game.dll", "?RefreshCooldown@SkillManager@GAME@@UEAAXH@Z", "SkillManager::RefreshCooldown(int)", false),
    ];

    public CompatibilityService(string? profilePath = null)
    {
        _profilePath = profilePath ?? Path.Combine(CompanionDataPaths.DirectoryPath, "compatibility.json");
    }

    public async Task<CompatibilityReport> VerifyAndUpdateAsync(GameInstallation game, CancellationToken cancellationToken = default)
    {
        var engineHash = await Task.Run(() => Hashing.Sha256File(game.EnginePath), cancellationToken);
        var gameHash = await Task.Run(() => Hashing.Sha256File(game.GameDllPath), cancellationToken);
        CompatibilityReport? previous = null;
        try
        {
            if (File.Exists(_profilePath))
            {
                await using var oldStream = File.OpenRead(_profilePath);
                previous = await JsonSerializer.DeserializeAsync<CompatibilityReport>(oldStream, JsonOptions, cancellationToken);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }

        var readers = new Dictionary<string, PeExportReader>(StringComparer.OrdinalIgnoreCase)
        {
            ["Engine.dll"] = new(game.EnginePath),
            ["Game.dll"] = new(game.GameDllPath),
        };
        var symbols = new List<SymbolVerification>();
        foreach (var definition in Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = readers[definition.Module].Find(definition.Name);
            var valid = found is not null && (definition.Executable ? found.IsExecutable : found.IsReadable) &&
                        found.Prologue.Length >= 8 && found.Prologue.Any(x => x is not (0x00 or 0xcc)) &&
                        (definition.Validator is null || definition.Validator(found.Prologue));
            symbols.Add(new(
                definition.Module, definition.Name, definition.FriendlyName, definition.Required,
                valid, found?.Rva ?? 0, found?.Section ?? "—",
                found is null ? "" : Hashing.Sha256Bytes(found.Prologue),
                found is null ? "Export not found" : valid ? $"Resolved by exported name and verified in {(definition.Executable ? "executable" : "readable data")} memory" : "Export did not pass section/body validation"));
        }

        var requiredFailed = symbols.Any(x => x.Required && !x.Found);
        var optionalMissing = symbols.Any(x => !x.Required && !x.Found);
        var updated = previous is null || !string.Equals(previous.EngineSha256, engineHash, StringComparison.OrdinalIgnoreCase) ||
                      !string.Equals(previous.GameDllSha256, gameHash, StringComparison.OrdinalIgnoreCase) ||
                      symbols.Any(current => previous.Symbols.FirstOrDefault(old => old.DecoratedName == current.DecoratedName)?.Fingerprint != current.Fingerprint);
        var report = new CompatibilityReport
        {
            VerifiedAt = DateTimeOffset.UtcNow,
            GameDirectory = game.RootDirectory,
            EngineSha256 = engineHash,
            GameDllSha256 = gameHash,
            Level = requiredFailed ? VerificationLevel.Failed : optionalMissing ? VerificationLevel.Warning : VerificationLevel.Passed,
            ProfileUpdated = updated && !requiredFailed,
            Symbols = symbols,
        };

        if (!requiredFailed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
            var temporary = _profilePath + ".new";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken);
            File.Move(temporary, _profilePath, true);
        }
        return report;
    }

    private static bool HasAreaNavigationPatterns(byte[] body)
    {
        var visibilityGate = false;
        var questBuilderCall = false;
        for (var index = 0; index + 8 < body.Length; index++)
            if (body[index] == 0x38 && body[index + 1] == 0x87 && body[index + 6] == 0x0f && body[index + 7] == 0x85)
                visibilityGate = true;
        // lea rcx,[rdi+field] / mov r9,rsi / lea r8,[rsp+20h] / call rel32
        for (var index = 0; index + 20 < body.Length; index++)
            if (body[index] == 0x48 && body[index + 1] == 0x8d && body[index + 2] == 0x8f &&
                body[index + 7] == 0x4c && body[index + 8] == 0x8b && body[index + 9] == 0xce &&
                body[index + 10] == 0x4c && body[index + 11] == 0x8d && body[index + 12] == 0x44 &&
                body[index + 13] == 0x24 && body[index + 14] == 0x20 && body[index + 15] == 0xe8)
                questBuilderCall = true;
        return visibilityGate && questBuilderCall;
    }

    private static bool HasTeleporterNuggetConstruction(byte[] body)
    {
        for (var index = 0; index + 16 < body.Length; index++)
            if (body[index] == 0x48 && body[index + 1] == 0x8d && body[index + 2] == 0x4c &&
                body[index + 3] == 0x24 && body[index + 4] == 0x30 && body[index + 5] == 0xe8 &&
                body[index + 10] == 0x90 && body[index + 11] == 0xc7 && body[index + 12] == 0x44 &&
                body[index + 13] == 0x24 && body[index + 14] == 0x38 && body[index + 15] == 0x03)
                return true;
        return false;
    }

    private sealed record SymbolDefinition(string Module, string Name, string FriendlyName, bool Required,
        bool Executable = true, Func<byte[], bool>? Validator = null);
}
