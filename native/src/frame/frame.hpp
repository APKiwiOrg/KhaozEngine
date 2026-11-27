#pragma once

#include "khaoz/khaoz_native.h"

#include <cstddef>
#include <memory>
#include <span>

struct khaoz_frame {
    explicit khaoz_frame(uint32_t capacity);

    [[nodiscard]] uint32_t capacity() const noexcept { return capacity_; }
    [[nodiscard]] std::span<const khaoz_draw_record> sorted_records() const noexcept;
    void submit(std::span<const khaoz_draw_record> records) noexcept;

  private:
    const uint32_t capacity_;
    std::unique_ptr<khaoz_draw_record[]> records_;
    std::unique_ptr<khaoz_draw_record[]> scratch_;
    std::size_t count_ = 0;
};
