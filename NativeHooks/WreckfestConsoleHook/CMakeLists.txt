cmake_minimum_required(VERSION 3.20)

project(WreckfestConsoleHook LANGUAGES CXX C)

include(FetchContent)

# MinHook gives us a trampoline. The target's entry point is patched once at
# install and once at uninstall, rather than being restored and re-patched around
# every call, which removes the window in which another game thread could execute
# the function while its first bytes were mid-rewrite.
#
# v1.3.3 ships MSVC/MinGW project files rather than a CMakeLists, so the sources
# are compiled here directly.
FetchContent_Declare(
    minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
    GIT_TAG        v1.3.3
)
FetchContent_MakeAvailable(minhook)

add_library(minhook STATIC
    ${minhook_SOURCE_DIR}/src/buffer.c
    ${minhook_SOURCE_DIR}/src/hook.c
    ${minhook_SOURCE_DIR}/src/trampoline.c
    ${minhook_SOURCE_DIR}/src/hde/hde64.c
    ${minhook_SOURCE_DIR}/src/hde/hde32.c
)
target_include_directories(minhook PUBLIC ${minhook_SOURCE_DIR}/include)
target_compile_definitions(minhook PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)

add_library(WreckfestConsoleHook SHARED
    WreckfestConsoleHook.cpp
)

target_link_libraries(WreckfestConsoleHook PRIVATE minhook)

target_compile_features(WreckfestConsoleHook PRIVATE cxx_std_20)
target_compile_definitions(WreckfestConsoleHook PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)

if(MSVC)
    set_property(TARGET WreckfestConsoleHook PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    set_property(TARGET minhook PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()
