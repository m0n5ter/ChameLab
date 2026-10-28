cmake_minimum_required(VERSION 3.24)
project(ChameLabHardnested C)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build configuration" FORCE)
endif()

include(FetchContent)
set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(XZ_TOOL_XZ OFF CACHE BOOL "" FORCE)
set(XZ_TOOL_XZDEC OFF CACHE BOOL "" FORCE)
set(XZ_TOOL_LZMADEC OFF CACHE BOOL "" FORCE)
set(XZ_TOOL_LZMAINFO OFF CACHE BOOL "" FORCE)
set(XZ_TOOL_SCRIPTS OFF CACHE BOOL "" FORCE)
set(XZ_DOC OFF CACHE BOOL "" FORCE)
set(XZ_NLS OFF CACHE BOOL "" FORCE)
set(XZ_DOXYGEN OFF CACHE BOOL "" FORCE)
FetchContent_Declare(xz
    GIT_REPOSITORY https://github.com/tukaani-project/xz
    GIT_TAG v5.8.1
    GIT_SHALLOW TRUE)
FetchContent_MakeAvailable(xz)

set(RECOVERY_DIR ${CMAKE_CURRENT_SOURCE_DIR}/..)
set(COMMON_DIR ${RECOVERY_DIR}/..)
add_library(chamelab-hardnested SHARED
    hardnested_api.c
    ${COMMON_DIR}/crypto1.c
    ${COMMON_DIR}/crapto1.c
    ${COMMON_DIR}/bucketsort.c
    ${COMMON_DIR}/parity.c
    ${RECOVERY_DIR}/cmdhfmfhard.c
    ${RECOVERY_DIR}/pm3/ui.c
    ${RECOVERY_DIR}/pm3/util.c
    ${RECOVERY_DIR}/pm3/commonutil.c
    ${RECOVERY_DIR}/hardnested/hardnested_bf_core.c
    ${RECOVERY_DIR}/hardnested/hardnested_bruteforce.c
    ${RECOVERY_DIR}/hardnested/hardnested_bitarray_core.c
    ${RECOVERY_DIR}/hardnested/tables.c)
if(NOT WIN32)
    target_sources(chamelab-hardnested PRIVATE ${RECOVERY_DIR}/pm3/util_posix.c)
endif()
target_include_directories(chamelab-hardnested PRIVATE
    ${COMMON_DIR} ${RECOVERY_DIR} ${RECOVERY_DIR}/pm3 ${RECOVERY_DIR}/hardnested
    ${xz_SOURCE_DIR}/src/liblzma/api)
target_link_libraries(chamelab-hardnested PRIVATE liblzma)
target_compile_definitions(chamelab-hardnested PRIVATE CHAMELAB_HARDNESTED)
if(MSVC)
    target_sources(chamelab-hardnested PRIVATE msvc/compiler.h msvc/pthread.h)
    target_include_directories(chamelab-hardnested PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/msvc)
    target_compile_options(chamelab-hardnested PRIVATE /FI${CMAKE_CURRENT_SOURCE_DIR}/msvc/compiler.h)
    target_compile_definitions(chamelab-hardnested PRIVATE _CRT_SECURE_NO_WARNINGS LZMA_API_STATIC)
else()
    find_package(Threads REQUIRED)
    target_link_libraries(chamelab-hardnested PRIVATE Threads::Threads m)
    target_compile_definitions(chamelab-hardnested PRIVATE _GNU_SOURCE)
    if(WIN32)
        target_sources(chamelab-hardnested PRIVATE ${COMMON_DIR}/compat/fmemopen/libfmemopen.c)
    endif()
endif()

add_executable(chamelab-hardnested-worker worker.c)
target_link_libraries(chamelab-hardnested-worker PRIVATE chamelab-hardnested)
set_target_properties(chamelab-hardnested-worker PROPERTIES
    INSTALL_RPATH "$ORIGIN;@loader_path")
install(TARGETS chamelab-hardnested chamelab-hardnested-worker
    RUNTIME DESTINATION . COMPONENT Hardnested LIBRARY DESTINATION . COMPONENT Hardnested)

enable_testing()
add_test(NAME missing_input COMMAND chamelab-hardnested-worker)
set_tests_properties(missing_input PROPERTIES WILL_FAIL TRUE)

add_executable(chamelab-hardnested-fixture EXCLUDE_FROM_ALL
    tests/generate_capture.c ${COMMON_DIR}/crypto1.c ${COMMON_DIR}/parity.c)
target_include_directories(chamelab-hardnested-fixture PRIVATE ${COMMON_DIR})
add_test(NAME generate_capture COMMAND chamelab-hardnested-fixture synthetic-nonces.txt)
set_tests_properties(generate_capture PROPERTIES FIXTURES_SETUP capture)
add_test(NAME recover_capture COMMAND chamelab-hardnested-worker 12345678 synthetic-nonces.txt)
set_tests_properties(recover_capture PROPERTIES
    FIXTURES_REQUIRED capture PASS_REGULAR_EXPRESSION "CHAMELAB_KEY=112233445566" TIMEOUT 180)
