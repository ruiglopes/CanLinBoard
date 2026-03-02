cmake_minimum_required(VERSION 3.13)

# ---- Pico SDK + FreeRTOS import (before project()) ----
set(PICO_BOARD "none" CACHE STRING "No predefined board")
set(PICO_PLATFORM "rp2350-arm-s" CACHE STRING "RP2350 ARM Secure")

# Auto-detect PICO_SDK_PATH from VS Code Pico extension if not set
if (NOT PICO_SDK_PATH AND NOT DEFINED ENV{PICO_SDK_PATH})
    file(GLOB _pico_sdk_versions "$ENV{USERPROFILE}/.pico-sdk/sdk/*")
    if (_pico_sdk_versions)
        list(SORT _pico_sdk_versions)
        list(GET _pico_sdk_versions -1 PICO_SDK_PATH)
        message("Auto-detected PICO_SDK_PATH: ${PICO_SDK_PATH}")
    endif()
endif()

# Auto-detect picotool from VS Code Pico extension if not set
if (NOT PICOTOOL_FETCH_FROM_GIT_PATH AND NOT DEFINED ENV{PICOTOOL_FETCH_FROM_GIT_PATH})
    file(GLOB _picotool_versions "$ENV{USERPROFILE}/.pico-sdk/picotool/*")
    if (_picotool_versions)
        list(SORT _picotool_versions)
        list(GET _picotool_versions -1 PICOTOOL_FETCH_FROM_GIT_PATH)
        message("Auto-detected PICOTOOL_FETCH_FROM_GIT_PATH: ${PICOTOOL_FETCH_FROM_GIT_PATH}")
    endif()
endif()

# Auto-detect FREERTOS_KERNEL_PATH from local lib/ if not set
if (NOT FREERTOS_KERNEL_PATH AND NOT DEFINED ENV{FREERTOS_KERNEL_PATH})
    set(_freertos_local "${CMAKE_CURRENT_SOURCE_DIR}/lib/FreeRTOS-Kernel")
    if (EXISTS "${_freertos_local}/include/FreeRTOS.h")
        set(FREERTOS_KERNEL_PATH "${_freertos_local}")
        message("Auto-detected FREERTOS_KERNEL_PATH: ${FREERTOS_KERNEL_PATH}")
    endif()
endif()

include(pico_sdk_import.cmake)
include(FreeRTOS_Kernel_import.cmake)

project(CanLinBoard C CXX ASM)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -Wall -Wextra -Werror")

# Initialize the Pico SDK
pico_sdk_init()

# Find Python for post-build scripts
find_package(Python3 REQUIRED COMPONENTS Interpreter)

# ---- can2040 library ----
add_library(can2040 STATIC
    lib/can2040/src/can2040.c
)
target_include_directories(can2040 PUBLIC
    lib/can2040/src
)
target_link_libraries(can2040 PUBLIC
    hardware_pio
    hardware_dma
    hardware_resets
    cmsis_core
)
# Third-party code: suppress warnings to avoid polluting build output
target_compile_options(can2040 PRIVATE -Wno-error -Wno-unused-parameter)

# ---- Main firmware executable ----
add_executable(${PROJECT_NAME}
    src/main.c
    src/app_header.c
    # Phase 1: HAL
    src/hal/hal_gpio.c
    src/hal/hal_spi.c
    src/hal/hal_flash_nvm.c
    src/hal/hal_clock.c
    # Phase 2: CAN
    src/can/can_manager.c
    # Phase 3: LIN
    src/lin/sja1124_driver.c
    src/lin/lin_manager.c
)

target_include_directories(${PROJECT_NAME} PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/config
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_link_libraries(${PROJECT_NAME}
    pico_stdlib
    hardware_spi
    hardware_pwm
    hardware_flash
    hardware_gpio
    hardware_watchdog
    hardware_timer
    hardware_clocks
    can2040
    FreeRTOS-Kernel
    FreeRTOS-Kernel-Heap4
)

# ---- Custom linker script ----
pico_set_linker_script(${PROJECT_NAME} ${CMAKE_CURRENT_SOURCE_DIR}/linker/app.ld)

# ---- No boot2 stage (bootloader handles it) ----
pico_set_binary_type(${PROJECT_NAME} no_flash)

# Disable USB/UART stdio (we manage our own IO)
pico_enable_stdio_usb(${PROJECT_NAME} 0)
pico_enable_stdio_uart(${PROJECT_NAME} 0)

# Generate .uf2, .bin, .hex, .map outputs
pico_add_extra_outputs(${PROJECT_NAME})

# ---- Post-build: patch app header in binary ----
add_custom_command(TARGET ${PROJECT_NAME} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E echo "Patching application header..."
    COMMAND ${Python3_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/tools/patch_header.py
            ${CMAKE_CURRENT_BINARY_DIR}/${PROJECT_NAME}.bin
    COMMENT "Patching CRC32 and size in app header"
    VERBATIM
)

# ---- Common source lists for test firmwares ----
set(COMMON_SOURCES
    src/app_header.c
    src/hal/hal_gpio.c
    src/hal/hal_spi.c
    src/hal/hal_flash_nvm.c
    src/hal/hal_clock.c
    src/can/can_manager.c
    src/lin/sja1124_driver.c
    src/lin/lin_manager.c
)

set(COMMON_INCLUDES
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/config
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

set(COMMON_LIBS
    pico_stdlib
    hardware_spi
    hardware_pwm
    hardware_flash
    hardware_gpio
    hardware_watchdog
    hardware_timer
    hardware_clocks
    can2040
    FreeRTOS-Kernel
    FreeRTOS-Kernel-Heap4
)

# ============================================================
# Test Firmwares (build with: cmake --build build --target <name>)
# ============================================================

# ---- Phase 1 Test: HAL ----
add_executable(test_phase1 EXCLUDE_FROM_ALL
    tests/phase1/test_hal.c
    ${COMMON_SOURCES}
)
target_include_directories(test_phase1 PRIVATE ${COMMON_INCLUDES})
target_link_libraries(test_phase1 ${COMMON_LIBS})
target_compile_definitions(test_phase1 PRIVATE TEST_PHASE1)
pico_set_linker_script(test_phase1 ${CMAKE_CURRENT_SOURCE_DIR}/linker/app.ld)
pico_set_binary_type(test_phase1 no_flash)
pico_enable_stdio_usb(test_phase1 0)
pico_enable_stdio_uart(test_phase1 0)
pico_add_extra_outputs(test_phase1)
add_custom_command(TARGET test_phase1 POST_BUILD
    COMMAND ${Python3_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/tools/patch_header.py
            ${CMAKE_CURRENT_BINARY_DIR}/test_phase1.bin
    VERBATIM
)

# ---- Phase 2 Test: CAN ----
add_executable(test_phase2 EXCLUDE_FROM_ALL
    tests/phase2/test_can.c
    ${COMMON_SOURCES}
)
target_include_directories(test_phase2 PRIVATE ${COMMON_INCLUDES})
target_link_libraries(test_phase2 ${COMMON_LIBS})
target_compile_definitions(test_phase2 PRIVATE TEST_PHASE2)
pico_set_linker_script(test_phase2 ${CMAKE_CURRENT_SOURCE_DIR}/linker/app.ld)
pico_set_binary_type(test_phase2 no_flash)
pico_enable_stdio_usb(test_phase2 0)
pico_enable_stdio_uart(test_phase2 0)
pico_add_extra_outputs(test_phase2)
add_custom_command(TARGET test_phase2 POST_BUILD
    COMMAND ${Python3_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/tools/patch_header.py
            ${CMAKE_CURRENT_BINARY_DIR}/test_phase2.bin
    VERBATIM
)

# ---- Phase 3 Test: LIN ----
add_executable(test_phase3 EXCLUDE_FROM_ALL
    tests/phase3/test_lin.c
    ${COMMON_SOURCES}
)
target_include_directories(test_phase3 PRIVATE ${COMMON_INCLUDES})
target_link_libraries(test_phase3 ${COMMON_LIBS})
target_compile_definitions(test_phase3 PRIVATE TEST_PHASE3)
pico_set_linker_script(test_phase3 ${CMAKE_CURRENT_SOURCE_DIR}/linker/app.ld)
pico_set_binary_type(test_phase3 no_flash)
pico_enable_stdio_usb(test_phase3 0)
pico_enable_stdio_uart(test_phase3 0)
pico_add_extra_outputs(test_phase3)
add_custom_command(TARGET test_phase3 POST_BUILD
    COMMAND ${Python3_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/tools/patch_header.py
            ${CMAKE_CURRENT_BINARY_DIR}/test_phase3.bin
    VERBATIM
)
