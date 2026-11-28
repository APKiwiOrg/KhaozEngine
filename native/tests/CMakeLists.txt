add_executable(khaoz_native_tests test_abi_guard.cpp test_frame.cpp test_vulkan_device.cpp)
target_include_directories(khaoz_native_tests PRIVATE ../src)
target_link_libraries(khaoz_native_tests PRIVATE khaoz_native_objects khaoz::doctest Threads::Threads)
add_test(NAME khaoz_native_abi COMMAND khaoz_native_tests)

# The managed loader test gives this fixture the production filename in an isolated child process.
add_library(khaoz_native_stale_abi_fixture SHARED stale_abi_fixture.cpp)
target_include_directories(khaoz_native_stale_abi_fixture PRIVATE ../include)
target_compile_definitions(khaoz_native_stale_abi_fixture PRIVATE KHAOZ_NATIVE_BUILD)
set_target_properties(khaoz_native_stale_abi_fixture PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY "${PROJECT_BINARY_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${PROJECT_BINARY_DIR}"
)

add_library(khaoz_native_vulkan_startup_fixture SHARED vulkan_startup_fixture.cpp)
target_include_directories(khaoz_native_vulkan_startup_fixture PRIVATE ../include)
target_compile_definitions(khaoz_native_vulkan_startup_fixture PRIVATE KHAOZ_NATIVE_BUILD VK_NO_PROTOTYPES)
target_link_libraries(khaoz_native_vulkan_startup_fixture PRIVATE khaoz::vulkan_headers)
set_target_properties(khaoz_native_vulkan_startup_fixture PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY "${PROJECT_BINARY_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${PROJECT_BINARY_DIR}"
)
add_dependencies(khaoz_native_tests khaoz_native_vulkan_startup_fixture)
target_compile_definitions(khaoz_native_tests PRIVATE KHAOZ_STARTUP_FIXTURE_PATH="$<TARGET_FILE:khaoz_native_vulkan_startup_fixture>")
target_link_libraries(khaoz_native_tests PRIVATE khaoz::volk khaoz::vma)
