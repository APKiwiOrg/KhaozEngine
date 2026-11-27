cmake_minimum_required(VERSION 3.28)
project(khaoz_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

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
