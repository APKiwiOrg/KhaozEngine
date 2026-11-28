#include <doctest/doctest.h>

#include "frame/frame.hpp"
#include "khaoz/khaoz_native.h"

#include <algorithm>
#include <array>
#include <memory>

namespace {

auto create_frame(uint32_t capacity) {
    khaoz_frame* frame = nullptr;
    REQUIRE(khaoz_frame_create(capacity, &frame) == KHAOZ_OK);
    return std::unique_ptr<khaoz_frame, decltype(&khaoz_frame_destroy)>(frame, khaoz_frame_destroy);
}

khaoz_draw_record draw_record(uint64_t key, uint32_t mesh = 0) {
    return {{1, 0, 0, static_cast<float>(mesh), 0, 1, 0, 0, 0, 0, 1, 0}, mesh, mesh + 1, key};
}

} // namespace

TEST_CASE("submit sorts by key and preserves the multiset") {
    const auto frame = create_frame(5);
    const std::array records{draw_record(3), draw_record(1), draw_record(3), draw_record(0), draw_record(2)};

    REQUIRE(khaoz_frame_submit(frame.get(), records.data(), static_cast<uint32_t>(records.size())) == KHAOZ_OK);
    std::array<uint64_t, records.size()> keys{};
    uint32_t count = 0;
    REQUIRE(khaoz_frame_sorted_keys(frame.get(), keys.data(), static_cast<uint32_t>(keys.size()), &count) == KHAOZ_OK);

    CHECK(count == keys.size());
    CHECK(keys == std::array<uint64_t, 5>{0, 1, 2, 3, 3});
}

TEST_CASE("submit with null records and non-zero count is rejected") {
    const auto frame = create_frame(1);
    const auto record = draw_record(7);
    REQUIRE(khaoz_frame_submit(frame.get(), &record, 1) == KHAOZ_OK);

    CHECK(khaoz_frame_submit(frame.get(), nullptr, 1) == KHAOZ_ERROR_INVALID_ARGUMENT);
    uint64_t key = 0;
    uint32_t count = 0;
    REQUIRE(khaoz_frame_sorted_keys(frame.get(), &key, 1, &count) == KHAOZ_OK);
    CHECK(count == 1);
    CHECK(key == record.sort_key);
}

TEST_CASE("submit above capacity is rejected and the previous frame survives") {
    const auto frame = create_frame(1);
    const auto original = draw_record(7);
    REQUIRE(khaoz_frame_submit(frame.get(), &original, 1) == KHAOZ_OK);

    const std::array oversized{draw_record(2), draw_record(1)};
    CHECK(khaoz_frame_submit(frame.get(), oversized.data(), static_cast<uint32_t>(oversized.size())) ==
          KHAOZ_ERROR_INVALID_ARGUMENT);
    uint64_t key = 0;
    uint32_t count = 0;
    REQUIRE(khaoz_frame_sorted_keys(frame.get(), &key, 1, &count) == KHAOZ_OK);
    CHECK(count == 1);
    CHECK(key == original.sort_key);
}

TEST_CASE("empty submit succeeds with zero keys") {
    const auto frame = create_frame(1);
    const auto record = draw_record(7);
    REQUIRE(khaoz_frame_submit(frame.get(), &record, 1) == KHAOZ_OK);

    REQUIRE(khaoz_frame_submit(frame.get(), nullptr, 0) == KHAOZ_OK);
    uint32_t count = 1;
    REQUIRE(khaoz_frame_sorted_keys(frame.get(), nullptr, 0, &count) == KHAOZ_OK);
    CHECK(count == 0);
}

TEST_CASE("sorted keys refuses an undersized destination without writing it") {
    const auto frame = create_frame(2);
    const std::array records{draw_record(4), draw_record(2)};
    REQUIRE(khaoz_frame_submit(frame.get(), records.data(), static_cast<uint32_t>(records.size())) == KHAOZ_OK);

    uint64_t key = 9;
    uint32_t count = 0;
    CHECK(khaoz_frame_sorted_keys(frame.get(), &key, 1, &count) == KHAOZ_ERROR_INVALID_ARGUMENT);
    CHECK(key == 9);
}

TEST_CASE("submit preserves the order of draw records with equal full-width keys") {
    constexpr uint64_t high_key = 0x8000000000000001ULL;
    constexpr std::array<uint64_t, 6> keys{high_key, 1, 256, high_key, 256, 1};
    constexpr uint32_t count = 96;
    std::array<khaoz_draw_record, count> records{};
    for (uint32_t index = 0; index < count; ++index) {
        records[index] = draw_record(keys[index % keys.size()], index);
    }
    auto expected = records;
    std::stable_sort(expected.begin(), expected.end(),
                     [](const auto& left, const auto& right) { return left.sort_key < right.sort_key; });
    const auto frame = create_frame(count);

    REQUIRE(khaoz_frame_submit(frame.get(), records.data(), count) == KHAOZ_OK);

    const auto sorted = frame->sorted_records();
    REQUIRE(sorted.size() == expected.size());
    for (std::size_t index = 0; index < expected.size(); ++index) {
        const auto& record = expected[index];
        CHECK(sorted[index].mesh_id == record.mesh_id);
        CHECK(sorted[index].material_id == record.material_id);
        CHECK(sorted[index].sort_key == record.sort_key);
        CHECK(std::ranges::equal(sorted[index].world, record.world));
    }
}
