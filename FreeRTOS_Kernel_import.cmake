# This is a copy of <FREERTOS_KERNEL_PATH>/portable/ThirdParty/GCC/RP2040/FreeRTOS_Kernel_import.cmake

if (DEFINED ENV{FREERTOS_KERNEL_PATH} AND (NOT FREERTOS_KERNEL_PATH))
    set(FREERTOS_KERNEL_PATH $ENV{FREERTOS_KERNEL_PATH})
    message("Using FREERTOS_KERNEL_PATH from environment ('${FREERTOS_KERNEL_PATH}')")
endif ()

set(FREERTOS_KERNEL_PATH "${FREERTOS_KERNEL_PATH}" CACHE PATH "Path to the FreeRTOS Kernel")

if (DEFINED ENV{FREERTOS_KERNEL_FETCH_FROM_GIT} AND (NOT FREERTOS_KERNEL_FETCH_FROM_GIT))
    set(FREERTOS_KERNEL_FETCH_FROM_GIT $ENV{FREERTOS_KERNEL_FETCH_FROM_GIT})
    message("Using FREERTOS_KERNEL_FETCH_FROM_GIT from environment ('${FREERTOS_KERNEL_FETCH_FROM_GIT}')")
endif ()

if (DEFINED ENV{FREERTOS_KERNEL_FETCH_FROM_GIT_PATH} AND (NOT FREERTOS_KERNEL_FETCH_FROM_GIT_PATH))
    set(FREERTOS_KERNEL_FETCH_FROM_GIT_PATH $ENV{FREERTOS_KERNEL_FETCH_FROM_GIT_PATH})
    message("Using FREERTOS_KERNEL_FETCH_FROM_GIT_PATH from environment ('${FREERTOS_KERNEL_FETCH_FROM_GIT_PATH}')")
endif ()

if (NOT FREERTOS_KERNEL_PATH)
    if (FREERTOS_KERNEL_FETCH_FROM_GIT)
        include(FetchContent)
        set(FETCHCONTENT_BASE_DIR_SAVE ${FETCHCONTENT_BASE_DIR})
        if (FREERTOS_KERNEL_FETCH_FROM_GIT_PATH)
            get_filename_component(FETCHCONTENT_BASE_DIR "${FREERTOS_KERNEL_FETCH_FROM_GIT_PATH}" REALPATH BASE_DIR "${CMAKE_SOURCE_DIR}")
        endif ()
        FetchContent_Declare(
                freertos_kernel
                GIT_REPOSITORY https://github.com/FreeRTOS/FreeRTOS-Kernel.git
                GIT_TAG main
        )
        if (NOT freertos_kernel)
            message("Downloading FreeRTOS Kernel")
            FetchContent_Populate(freertos_kernel)
            set(FREERTOS_KERNEL_PATH ${freertos_kernel_SOURCE_DIR})
        endif ()
        set(FETCHCONTENT_BASE_DIR ${FETCHCONTENT_BASE_DIR_SAVE})
    else ()
        message(FATAL_ERROR
                "FreeRTOS Kernel location was not specified. Please set FREERTOS_KERNEL_PATH or set FREERTOS_KERNEL_FETCH_FROM_GIT to on to fetch from git."
                )
    endif ()
endif ()

get_filename_component(FREERTOS_KERNEL_PATH "${FREERTOS_KERNEL_PATH}" REALPATH BASE_DIR "${CMAKE_BINARY_DIR}")
if (NOT EXISTS ${FREERTOS_KERNEL_PATH})
    message(FATAL_ERROR "Directory '${FREERTOS_KERNEL_PATH}' not found")
endif ()

set(FREERTOS_KERNEL_PATH ${FREERTOS_KERNEL_PATH} CACHE PATH "Path to the FreeRTOS Kernel" FORCE)

if (PICO_PLATFORM STREQUAL "rp2350-arm-s")
    include(${FREERTOS_KERNEL_PATH}/portable/ThirdParty/GCC/RP2350_ARM_NTZ/library.cmake)
else()
    include(${FREERTOS_KERNEL_PATH}/portable/ThirdParty/GCC/RP2040/library.cmake)
endif()
