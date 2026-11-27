find_package(doctest CONFIG REQUIRED)

add_executable(khaoz_native_tests test_abi_guard.cpp)
target_include_directories(khaoz_native_tests PRIVATE ../src)
target_link_libraries(khaoz_native_tests PRIVATE khaoz_native_objects doctest::doctest Threads::Threads)
add_test(NAME khaoz_native_abi COMMAND khaoz_native_tests)
