#include "frame.hpp"

#include "../abi/abi_guard.hpp"

#include <algorithm>
#include <array>
#include <cstddef>

static_assert(sizeof(khaoz_draw_record) == 64);
static_assert(offsetof(khaoz_draw_record, world) == 0);
static_assert(offsetof(khaoz_draw_record, mesh_id) == 48);
static_assert(offsetof(khaoz_draw_record, material_id) == 52);
static_assert(offsetof(khaoz_draw_record, sort_key) == 56);

namespace {

uint8_t key_byte(uint64_t key, unsigned shift) noexcept { return static_cast<uint8_t>(key >> shift); }

void sort_key_byte(std::span<const khaoz_draw_record> source, khaoz_draw_record* destination, unsigned shift) noexcept {
    std::array<uint32_t, 256> offsets{};
    for (const auto& record : source) {
        ++offsets[key_byte(record.sort_key, shift)];
    }

    uint32_t next = 0;
    for (auto& offset : offsets) {
        const auto count = offset;
        offset = next;
        next += count;
    }

    // Walking source order keeps ties stable in each least-significant-byte pass.
    for (const auto& record : source) {
        destination[offsets[key_byte(record.sort_key, shift)]++] = record;
    }
}

khaoz_result invalid_argument(const char* message) noexcept {
    khaoz::abi::set_last_error(message);
    return KHAOZ_ERROR_INVALID_ARGUMENT;
}

} // namespace

khaoz_frame::khaoz_frame(uint32_t capacity)
    : capacity_(capacity), records_(std::make_unique_for_overwrite<khaoz_draw_record[]>(capacity)),
      scratch_(std::make_unique_for_overwrite<khaoz_draw_record[]>(capacity)) {}

std::span<const khaoz_draw_record> khaoz_frame::sorted_records() const noexcept { return {records_.get(), count_}; }

void khaoz_frame::submit(std::span<const khaoz_draw_record> records) noexcept {
    if (records.empty()) {
        count_ = 0;
        return;
    }

    const auto first_key = records.front().sort_key;
    uint64_t varying_bits = 0;
    for (const auto& record : records) {
        varying_bits |= record.sort_key ^ first_key;
    }

    if (varying_bits == 0) {
        std::copy(records.begin(), records.end(), records_.get());
    } else {
        // Constant key bytes cannot change the order. Each active pass swaps the two owned buffers.
        for (unsigned shift = 0; varying_bits != 0; shift += 8u, varying_bits >>= 8u) {
            if ((varying_bits & 0xffu) == 0) {
                continue;
            }

            sort_key_byte(records, scratch_.get(), shift);
            records_.swap(scratch_);
            records = {records_.get(), records.size()};
        }
    }

    count_ = records.size();
}

khaoz_result khaoz_frame_create(uint32_t capacity, khaoz_frame** out_frame) {
    return khaoz::abi::guard([&] {
        if (out_frame == nullptr) {
            return invalid_argument("The frame output pointer is required");
        }

        *out_frame = nullptr;
        *out_frame = new khaoz_frame(capacity);
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_frame_submit(khaoz_frame* frame, const khaoz_draw_record* records, uint32_t count) {
    return khaoz::abi::guard([&] {
        if (frame == nullptr) {
            return invalid_argument("The frame is required");
        }
        if (records == nullptr && count != 0) {
            return invalid_argument("Draw records are required for a non-empty submit");
        }
        if (count > frame->capacity()) {
            return invalid_argument("The draw record count exceeds the frame capacity");
        }

        frame->submit({records, count});
        return KHAOZ_OK;
    });
}

khaoz_result khaoz_frame_sorted_keys(const khaoz_frame* frame, uint64_t* out_keys, uint32_t capacity,
                                     uint32_t* out_count) {
    return khaoz::abi::guard([&] {
        if (frame == nullptr || out_count == nullptr) {
            return invalid_argument("The frame and key count output pointer are required");
        }

        const auto records = frame->sorted_records();
        if (records.size() > capacity || (out_keys == nullptr && !records.empty())) {
            return invalid_argument("The destination must have room for every sorted key");
        }

        for (const auto& record : records) {
            *out_keys++ = record.sort_key;
        }
        *out_count = static_cast<uint32_t>(records.size());
        return KHAOZ_OK;
    });
}

void khaoz_frame_destroy(khaoz_frame* frame) {
    khaoz::abi::guard([&] {
        delete frame;
        return KHAOZ_OK;
    });
}
