#pragma once

// Exact PE identities for game images whose exported APIs, object layouts and
// internal UI/render adapters have been audited.  Unknown or partially updated
// installations keep only export-safe bridge capabilities.
namespace SupportedBuilds
{
    enum class Build { Unknown, V1308, V1311 };

    inline Build Identify(HMODULE module, DWORD oldTimestamp, DWORD oldSize,
        DWORD currentTimestamp, DWORD currentSize)
    {
        if (!module) return Build::Unknown;
        const auto bytes = reinterpret_cast<const unsigned char*>(module);
        const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(bytes);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return Build::Unknown;
        const auto pe = reinterpret_cast<const IMAGE_NT_HEADERS64*>(bytes + dos->e_lfanew);
        if (pe->Signature != IMAGE_NT_SIGNATURE) return Build::Unknown;
        if (pe->FileHeader.TimeDateStamp == oldTimestamp && pe->OptionalHeader.SizeOfImage == oldSize)
            return Build::V1308;
        if (pe->FileHeader.TimeDateStamp == currentTimestamp && pe->OptionalHeader.SizeOfImage == currentSize)
            return Build::V1311;
        return Build::Unknown;
    }

    inline Build Executable(HMODULE module) { return Identify(module, 0x6A85FBEC, 0x482000, 0x6AC7F680, 0x482000); }
    inline Build Engine(HMODULE module) { return Identify(module, 0x6A85FB5B, 0x450000, 0x6AC7F5F7, 0x450000); }
    inline Build Game(HMODULE module) { return Identify(module, 0x6A85FBB3, 0xAB5000, 0x6AC7F64B, 0xAE6000); }
    inline bool Matching(HMODULE engine, HMODULE game)
    {
        const auto engineBuild = Engine(engine), gameBuild = Game(game);
        return engineBuild != Build::Unknown && engineBuild == gameBuild;
    }
}
