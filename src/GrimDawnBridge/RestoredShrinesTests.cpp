#include "RestoredShrines.h"
#include <array>
#include <cstdio>
#include <cstring>

namespace
{
    void Require(bool condition, const char* message)
    {
        if (condition) return;
        std::fprintf(stderr, "FAILED: %s\n", message);
        std::exit(1);
    }
}

int main()
{
    std::array<char, 512> response{};

    Require(RestoredShrines::FormatResponse(nullptr, nullptr, response.data(), response.size()),
        "null empty span formats zero restored shrines");
    Require(std::strcmp(response.data(), "OK RESTORED_SHRINES 0") == 0,
        "null empty span produces zero header");

    const std::array<unsigned char, 32> twoUids
    {
        0x9D, 0xAD, 0x78, 0x65, 0x06, 0x45, 0xD9, 0x9B, 0x7D, 0xE6, 0xED, 0xB1, 0xC3, 0x2E, 0x5F, 0x80,
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE
    };
    Require(RestoredShrines::FormatResponse(twoUids.data(), twoUids.data(), response.data(), response.size()),
        "empty non-null span formats zero restored shrines");
    Require(std::strcmp(response.data(), "OK RESTORED_SHRINES 0") == 0,
        "empty non-null span produces zero header");

    Require(RestoredShrines::FormatResponse(twoUids.data(), twoUids.data() + twoUids.size(), response.data(), response.size()),
        "two 16-byte UniqueIds format cleanly");
    Require(std::strcmp(response.data(),
        "OK RESTORED_SHRINES 2\t9DAD78650645D99B7DE6EDB1C32E5F80\t0123456789ABCDEF1032547698BADCFE") == 0,
        "formatted response preserves verbatim 16-byte UID hex order");

    Require(!RestoredShrines::FormatResponse(twoUids.data(), twoUids.data() + 15, response.data(), response.size()),
        "misaligned 15-byte span is rejected");
    Require(!RestoredShrines::FormatResponse(twoUids.data(), twoUids.data() + 17, response.data(), response.size()),
        "misaligned 17-byte span is rejected");
    Require(!RestoredShrines::FormatResponse(twoUids.data() + 16, twoUids.data(), response.data(), response.size()),
        "reversed span is rejected");

    std::array<unsigned char, (RestoredShrines::MaxUniqueIds + 1) * RestoredShrines::UniqueIdSize> overflow{};
    Require(!RestoredShrines::FormatResponse(overflow.data(), overflow.data() + overflow.size(), response.data(), response.size()),
        "excessive UniqueId count is rejected");

    std::array<char, 66> small{};
    Require(RestoredShrines::FormatResponse(twoUids.data(), twoUids.data() + twoUids.size(), small.data(), small.size()),
        "bounded buffer truncates cleanly to fitting UID count");
    Require(std::strcmp(small.data(), "OK RESTORED_SHRINES 1\t9DAD78650645D99B7DE6EDB1C32E5F80") == 0,
        "bounded buffer emits exact header count without overflow");

    Require(RestoredShrines::FormatResponse(nullptr, nullptr, response.data(), response.size(), 0) &&
            std::strcmp(response.data(), "OK RESTORED_SHRINES 0 DIFF=0") == 0,
        "empty span formats valid zero difficulty in header");
    Require(RestoredShrines::FormatResponse(twoUids.data(), twoUids.data() + twoUids.size(), response.data(), response.size(), 2) &&
            std::strcmp(response.data(),
                "OK RESTORED_SHRINES 2 DIFF=2\t9DAD78650645D99B7DE6EDB1C32E5F80\t0123456789ABCDEF1032547698BADCFE") == 0,
        "valid difficulty appends DIFF= to multi-UID header");
    Require(RestoredShrines::FormatResponse(nullptr, nullptr, response.data(), response.size(), 3) &&
            std::strcmp(response.data(), "OK RESTORED_SHRINES 0") == 0,
        "out-of-range difficulty omits DIFF= from header");
    Require(RestoredShrines::FormatResponse(twoUids.data(), twoUids.data() + twoUids.size(), small.data(), small.size(), 0) &&
            std::strcmp(small.data(), "OK RESTORED_SHRINES 1 DIFF=0\t9DAD78650645D99B7DE6EDB1C32E5F80") == 0,
        "bounded buffer with difficulty emits exact header count without overflow");

    std::puts("PASS  RestoredShrines native formatting tests");
    return 0;
}
