#pragma once
#include <cstdio>
#include <cstddef>

namespace RestoredShrines
{
    constexpr size_t UniqueIdSize = 16;
    constexpr size_t MaxUniqueIds = 256;

    // Formats raw 16-byte UniqueId spans as uppercase hex tokens without C++
    // locals that require unwinding inside SEH __try wrappers.
    inline bool FormatResponse(const unsigned char* begin, const unsigned char* end, char* response, size_t responseSize, int difficulty = -1)
    {
        if (!response || responseSize < 32) return false;
        size_t count = 0;
        if (begin || end)
        {
            if (!begin || !end || end < begin) return false;
            const auto byteCount = static_cast<size_t>(end - begin);
            if (byteCount % UniqueIdSize != 0) return false;
            count = byteCount / UniqueIdSize;
            if (count > MaxUniqueIds) return false;
        }
        const auto maxFit = (responseSize - 32) / 33;
        const auto emitted = count < maxFit ? count : maxFit;
        int pos = (difficulty >= 0 && difficulty <= 2)
            ? std::snprintf(response, responseSize, "OK RESTORED_SHRINES %zu DIFF=%d", emitted, difficulty)
            : std::snprintf(response, responseSize, "OK RESTORED_SHRINES %zu", emitted);
        if (pos <= 0 || static_cast<size_t>(pos) >= responseSize) return false;
        static constexpr char kHex[] = "0123456789ABCDEF";
        for (size_t i = 0; i < emitted; ++i)
        {
            if (static_cast<size_t>(pos) + 33 >= responseSize) return false;
            response[pos++] = '\t';
            for (size_t b = 0; b < UniqueIdSize; ++b)
            {
                const unsigned char byte = begin[i * UniqueIdSize + b];
                response[pos++] = kHex[(byte >> 4) & 0xF];
                response[pos++] = kHex[byte & 0xF];
            }
        }
        response[pos] = '\0';
        return true;
    }
}
