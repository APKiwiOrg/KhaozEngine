cmake_minimum_required(VERSION 3.28)
project(khaoz_native LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

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
    add_link_options(-static-libstdc++ -static-libgcc)
endif()

find_package(Threads REQUIRED)

# Tests use the same objects as the shared library so the internal ABI guard stays hidden.
add_library(khaoz_native_objects OBJECT
    src/abi/abi_version.cpp
    src/abi/last_error.cpp
)
set_target_properties(khaoz_native_objects PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_compile_definitions(khaoz_native_objects PRIVATE KHAOZ_NATIVE_BUILD)
target_include_directories(khaoz_native_objects PUBLIC include)
target_link_libraries(khaoz_native_objects PRIVATE Threads::Threads)

add_library(khaoz_native SHARED)
target_link_libraries(khaoz_native PRIVATE khaoz_native_objects)
target_include_directories(khaoz_native PUBLIC include)
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    target_link_options(khaoz_native PRIVATE "LINKER:--exclude-libs,ALL")
endif()

include(CTest)
if(BUILD_TESTING)
    add_subdirectory(tests)
endif()
