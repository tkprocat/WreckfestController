cmake_minimum_required(VERSION 3.20)

project(WreckfestConsoleHook LANGUAGES CXX)

add_library(WreckfestConsoleHook SHARED
    WreckfestConsoleHook.cpp
)

target_compile_features(WreckfestConsoleHook PRIVATE cxx_std_20)
target_compile_definitions(WreckfestConsoleHook PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)

if(MSVC)
    set_property(TARGET WreckfestConsoleHook PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()
