cmake_minimum_required(VERSION 3.28)
project(khaoz_native LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
# The project uses no C++20 modules. Without this, CMake 3.28 and later scan every C++20 source and add
# a module map response file to each compile command, which the include check refuses.
set(CMAKE_CXX_SCAN_FOR_MODULES OFF)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

# Release builds carry debug information, so CI can publish it as the symbols artifact before it strips
# the shipped library. MSVC keeps it in the objects and links a separate PDB. /DEBUG would turn off
# unreferenced-code removal and folding, so they are turned back on.
if(MSVC)
    set(CMAKE_MSVC_DEBUG_INFORMATION_FORMAT Embedded)
    add_link_options(/DEBUG /OPT:REF /OPT:ICF)
else()
    add_compile_options(-g)
endif()

# Compile the unmodified upstream C source before adding the engine warning flags.
set(third_party "${CMAKE_CURRENT_SOURCE_DIR}/third_party")
add_library(khaoz_native_doctest INTERFACE)
add_library(khaoz::doctest ALIAS khaoz_native_doctest)
target_include_directories(khaoz_native_doctest SYSTEM INTERFACE "${third_party}/doctest")

add_library(khaoz_native_vulkan_headers INTERFACE)
add_library(khaoz::vulkan_headers ALIAS khaoz_native_vulkan_headers)
target_include_directories(khaoz_native_vulkan_headers SYSTEM INTERFACE "${third_party}/vulkan-headers/include")

add_library(khaoz_native_volk STATIC "${third_party}/volk/volk.c")
add_library(khaoz::volk ALIAS khaoz_native_volk)
set_target_properties(khaoz_native_volk PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_compile_definitions(khaoz_native_volk PUBLIC VK_NO_PROTOTYPES)
target_include_directories(khaoz_native_volk SYSTEM PUBLIC "${third_party}/volk")
target_link_libraries(khaoz_native_volk PUBLIC khaoz::vulkan_headers PRIVATE ${CMAKE_DL_LIBS})

add_library(khaoz_native_vma INTERFACE)
add_library(khaoz::vma ALIAS khaoz_native_vma)
target_include_directories(khaoz_native_vma SYSTEM INTERFACE "${third_party}/vma/include")
target_link_libraries(khaoz_native_vma INTERFACE khaoz::vulkan_headers)

if(MSVC)
    add_compile_options(/W4 /WX /permissive-)
else()
    add_compile_options(-Wall -Wextra -Wpedantic -Werror)
endif()

if(CMAKE_SYSTEM_NAME STREQUAL "Darwin")
    add_compile_definitions(_LIBCPP_HARDENING_MODE=_LIBCPP_HARDENING_MODE_EXTENSIVE)
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    add_compile_definitions(_GLIBCXX_ASSERTIONS)
endif()

find_package(Threads REQUIRED)

# Tests use the same objects as the shared library so the internal ABI guard stays hidden.
add_library(khaoz_native_objects OBJECT
    src/abi/abi_version.cpp
    src/abi/last_error.cpp
    src/frame/frame.cpp
    src/gpu/vulkan/device.cpp
    src/gpu/vulkan/adapter.cpp
    src/gpu/vulkan/capabilities.cpp
    src/gpu/vulkan/instance.cpp
    src/gpu/vulkan/loader.cpp
    src/gpu/vulkan/validation.cpp
    src/gpu/vulkan/memory.cpp
    src/gpu/vulkan/buffer.cpp
    src/gpu/vulkan/commands.cpp
    src/gpu/vulkan/fence.cpp
    src/gpu/vulkan/texture.cpp
    src/gpu/vulkan/sampler.cpp
    src/gpu/vulkan/transfer.cpp
    src/gpu/vulkan/shaders.cpp
    src/gpu/vulkan/resource_layout.cpp
    src/gpu/vulkan/resource_set.cpp
    src/gpu/vulkan/pipeline.cpp
    src/gpu/vulkan/pipeline_state.cpp
    src/gpu/vulkan/framebuffer.cpp
    src/gpu/vulkan/rendering.cpp
    src/gpu/vulkan/draw.cpp
)
set_target_properties(khaoz_native_objects PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_compile_definitions(khaoz_native_objects PRIVATE KHAOZ_NATIVE_BUILD)
target_include_directories(khaoz_native_objects PUBLIC include)
target_include_directories(khaoz_native_objects PRIVATE src)
target_compile_definitions(khaoz_native_objects PRIVATE VMA_STATIC_VULKAN_FUNCTIONS=0 VMA_DYNAMIC_VULKAN_FUNCTIONS=1)
target_link_libraries(khaoz_native_objects PRIVATE Threads::Threads khaoz::volk khaoz::vulkan_headers khaoz::vma)

add_library(khaoz_native SHARED)
target_link_libraries(khaoz_native PRIVATE khaoz_native_objects)
target_include_directories(khaoz_native PUBLIC include)
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    # Only the shipped library carries the C++ runtime statically, so it loads on hosts with an older
    # libstdc++. Tests and fixtures use the shared runtime: a fixture with its own static copy would
    # allocate a private exception pool at load that no dlclose frees, which LeakSanitizer reports.
    target_link_options(khaoz_native PRIVATE -static-libstdc++ -static-libgcc "LINKER:--exclude-libs,ALL")
endif()

include(CTest)
if(BUILD_TESTING)
    add_subdirectory(tests)
endif()
