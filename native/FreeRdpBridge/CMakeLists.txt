cmake_minimum_required(VERSION 3.20)
project(FreeRdpBridge C)

# FreeRDP install prefix (contains lib/cmake/FreeRDP3, FreeRDP-Client3, WinPR3)
set(FREERDP_INSTALL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../../FreeRDP/build/install"
    CACHE PATH "FreeRDP install prefix")
# Dependencies FreeRDP was built with (zlib, openssl, ...): the vcpkg installed tree
set(FREERDP_DEPS_DIR "${FREERDP_INSTALL_DIR}/../vcpkg_installed/x64-windows"
    CACHE PATH "Prefix of the FreeRDP dependencies (vcpkg installed dir)")
list(APPEND CMAKE_PREFIX_PATH "${FREERDP_INSTALL_DIR}" "${FREERDP_DEPS_DIR}")

find_package(WinPR REQUIRED)
find_package(FreeRDP REQUIRED)
find_package(FreeRDP-Client REQUIRED)

add_library(FreeRdpBridge SHARED rdp_bridge.c rdp_bridge.h)
set_property(TARGET FreeRdpBridge PROPERTY C_STANDARD 11)
target_compile_definitions(FreeRdpBridge PRIVATE _CRT_SECURE_NO_WARNINGS WIN32_LEAN_AND_MEAN)
target_link_libraries(FreeRdpBridge PRIVATE freerdp-client freerdp winpr ws2_32 user32 gdi32)

install(TARGETS FreeRdpBridge RUNTIME DESTINATION bin)
