cmake_minimum_required(VERSION 3.24)
project(ChameLabHardnested C)
option(CHAMELAB_EMBEDDED "Build the bounded, cancellable single-thread mobile/WebAssembly engine" OFF)

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
if(CHAMELAB_EMBEDDED)
    set(XZ_THREADS no CACHE STRING "Embedded solver uses one thread" FORCE)
endif()
FetchContent_Declare(xz
    GIT_REPOSITORY https://github.com/tukaani-project/xz
    GIT_TAG v5.8.1
    GIT_SHALLOW TRUE)
FetchContent_MakeAvailable(xz)

set(RECOVERY_DIR ${CMAKE_CURRENT_SOURCE_DIR}/..)
set(COMMON_DIR ${RECOVERY_DIR}/..)
set(HARDNESTED_LIBRARY_TYPE SHARED)
if(CHAMELAB_EMBEDDED AND (IOS OR EMSCRIPTEN))
    set(HARDNESTED_LIBRARY_TYPE STATIC)
endif()
add_library(chamelab-hardnested ${HARDNESTED_LIBRARY_TYPE}
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

if(CHAMELAB_EMBEDDED)
    if(MSVC)
        message(FATAL_ERROR "Embedded builds require Clang/GCC (Android NDK, Xcode or Emscripten)")
    endif()
    target_sources(chamelab-hardnested PRIVATE embedded_runtime.c)
    set_source_files_properties(embedded_runtime.c PROPERTIES COMPILE_DEFINITIONS HN_RUNTIME_IMPLEMENTATION)
    target_include_directories(chamelab-hardnested PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    target_compile_definitions(chamelab-hardnested PRIVATE CHAMELAB_EMBEDDED)
    target_compile_options(chamelab-hardnested PRIVATE
        -include ${CMAKE_CURRENT_SOURCE_DIR}/embedded_runtime.h -O3)
    set_target_properties(chamelab-hardnested PROPERTIES OUTPUT_NAME chamelab-hardnested-embedded)
    if(NOT EMSCRIPTEN)
        install(TARGETS chamelab-hardnested
            ARCHIVE DESTINATION . COMPONENT Hardnested
            LIBRARY DESTINATION . COMPONENT Hardnested)
    endif()
    if(IOS)
        install(TARGETS liblzma ARCHIVE DESTINATION . COMPONENT Hardnested)
    endif()
    add_executable(hardnested-embedded-tests EXCLUDE_FROM_ALL tests/embedded_test.c)
    target_include_directories(hardnested-embedded-tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    target_link_libraries(hardnested-embedded-tests PRIVATE chamelab-hardnested)
    if(EMSCRIPTEN)
        # Use native Wasm exception handling for safe setjmp/longjmp unwinding.
        target_compile_options(chamelab-hardnested PRIVATE -fwasm-exceptions -sSUPPORT_LONGJMP=wasm)
        set_target_properties(hardnested-embedded-tests PROPERTIES SUFFIX .cjs)
        target_link_options(hardnested-embedded-tests PRIVATE -O3
            -fwasm-exceptions -sSUPPORT_LONGJMP=wasm
            -sSINGLE_FILE=1 -sENVIRONMENT=node -sNODERAWFS=1 -sEXIT_RUNTIME=1
            -sALLOW_MEMORY_GROWTH=1 -sMAXIMUM_MEMORY=2147483648
            -sSTACK_SIZE=8388608 -sABORTING_MALLOC=0)
        add_executable(hardnested-wasm wasm_entry.c)
        target_link_libraries(hardnested-wasm PRIVATE chamelab-hardnested)
        set_target_properties(hardnested-wasm PROPERTIES OUTPUT_NAME hardnested SUFFIX .js)
        target_link_options(hardnested-wasm PRIVATE
            --no-entry -O3 -fwasm-exceptions -sSUPPORT_LONGJMP=wasm
            "-sEXPORTED_FUNCTIONS=['_chamelab_hardnested_embedded','_malloc','_free']"
            "-sEXPORTED_RUNTIME_METHODS=['FS','ccall']"
            -sMODULARIZE=1 -sEXPORT_ES6=1 -sENVIRONMENT=worker,node
            -sALLOW_MEMORY_GROWTH=1 -sMAXIMUM_MEMORY=2147483648
            -sSTACK_SIZE=8388608 -sABORTING_MALLOC=0 -sFILESYSTEM=1)
        install(FILES ${CMAKE_CURRENT_BINARY_DIR}/hardnested.js ${CMAKE_CURRENT_BINARY_DIR}/hardnested.wasm
            DESTINATION . COMPONENT Hardnested)
    endif()
    return()
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
