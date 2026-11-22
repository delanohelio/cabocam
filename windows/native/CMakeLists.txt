cmake_minimum_required(VERSION 3.21)
project(CaboCamNative LANGUAGES CXX)

if(NOT WIN32)
  message(FATAL_ERROR "CaboCam native components build only on Windows (MSVC, x64).")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Static CRT: the DLLs run inside the Frame Server and CaboCam.exe without needing the
# Visual C++ Redistributable installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Both DLLs land in <build>/bin/<Config>/ (multi-config generators append the config).
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_PDB_OUTPUT_DIRECTORY     ${CMAKE_BINARY_DIR}/bin)

add_compile_definitions(UNICODE _UNICODE NOMINMAX WINVER=0x0A00 _WIN32_WINNT=0x0A00
                        _CRT_SECURE_NO_WARNINGS)

add_library(cabocam_common INTERFACE)
target_include_directories(cabocam_common INTERFACE ${CMAKE_CURRENT_SOURCE_DIR}/common)
if(MSVC)
  target_compile_options(cabocam_common INTERFACE /W4 /permissive- /utf-8 /Zc:__cplusplus /EHsc)
endif()

# ---------------------------------------------------------------------------
# CaboCam.VirtualCamera.dll  (COM media source loaded by the Windows Frame Server)
# ---------------------------------------------------------------------------
add_library(CaboCamVirtualCamera SHARED
  VirtualCamera/dllmain.cpp
  VirtualCamera/Activate.cpp
  VirtualCamera/MediaSource.cpp
  VirtualCamera/SharedFrameSource.cpp
  VirtualCamera/Nv12Scaler.cpp
  VirtualCamera/CaboCam.VirtualCamera.def
)
set_target_properties(CaboCamVirtualCamera PROPERTIES OUTPUT_NAME "CaboCam.VirtualCamera")
target_link_libraries(CaboCamVirtualCamera PRIVATE cabocam_common
  mfplat mf mfuuid ole32 oleaut32 uuid advapi32 shlwapi)

# ---------------------------------------------------------------------------
# CaboCam.Native.dll  (flat C API for P/Invoke from CaboCam.exe)
# ---------------------------------------------------------------------------
add_library(CaboCamNativeLib SHARED
  Native/vcam.cpp
  Native/sink.cpp
  Native/decoder.cpp
  Native/convert.cpp
)
set_target_properties(CaboCamNativeLib PROPERTIES OUTPUT_NAME "CaboCam.Native")
target_compile_definitions(CaboCamNativeLib PRIVATE CABOCAM_NATIVE_EXPORTS)
target_link_libraries(CaboCamNativeLib PRIVATE cabocam_common
  mfplat mf mfuuid mfreadwrite mfsensorgroup ksuser ole32 oleaut32 uuid advapi32 shlwapi)
