cmake_minimum_required(VERSION 3.10)
project(memoryreadingdll CXX)

# set c++ standard
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

# force 32-bit build
set(CMAKE_GENERATOR_PLATFORM Win32)

# force static linking for all libraries - this must be set before any targets
if(MSVC)
    # replace /MD with /MT for static runtime linking
    foreach(flag_var CMAKE_CXX_FLAGS CMAKE_CXX_FLAGS_DEBUG CMAKE_CXX_FLAGS_RELEASE CMAKE_CXX_FLAGS_MINSIZEREL CMAKE_CXX_FLAGS_RELWITHDEBINFO)
        if(${flag_var} MATCHES "/MD")
            string(REGEX REPLACE "/MD" "/MT" ${flag_var} "${${flag_var}}")
        endif()
    endforeach()
    # add /MT if not present
    if(NOT CMAKE_CXX_FLAGS MATCHES "/MT")
        set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} /MT")
    endif()
    if(NOT CMAKE_CXX_FLAGS_RELEASE MATCHES "/MT")
        set(CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE} /MT")
    endif()
endif()

# add source files
add_library(memoryreadingdll SHARED
    dllmain.cpp
    memreader.cpp
    ZeroMQPublisher.cpp
    version.rc
)

# manually find and link zeromq static library
find_path(ZMQ_INCLUDE_DIR zmq.h 
    PATHS ${CMAKE_CURRENT_SOURCE_DIR}/../vcpkg/installed/x86-windows-static/include)

find_library(ZMQ_STATIC_LIB 
    NAMES libzmq-mt-s-4_3_5 libzmq-mt-s
    PATHS ${CMAKE_CURRENT_SOURCE_DIR}/../vcpkg/installed/x86-windows-static/lib
    NO_DEFAULT_PATH)

if(NOT ZMQ_INCLUDE_DIR OR NOT ZMQ_STATIC_LIB)
    message(FATAL_ERROR "Static ZeroMQ not found. Include: ${ZMQ_INCLUDE_DIR}, Lib: ${ZMQ_STATIC_LIB}")
endif()

message(STATUS "Found ZeroMQ static lib: ${ZMQ_STATIC_LIB}")
message(STATUS "Found ZeroMQ include: ${ZMQ_INCLUDE_DIR}")

# include zeromq headers
target_include_directories(memoryreadingdll PRIVATE ${ZMQ_INCLUDE_DIR})

# define ZMQ_STATIC for static linking
target_compile_definitions(memoryreadingdll PRIVATE ZMQ_STATIC)

# link zeromq static library and all its dependencies
target_link_libraries(memoryreadingdll PRIVATE 
    ${ZMQ_STATIC_LIB}
    kernel32 
    ws2_32 
    version
    iphlpapi
    rpcrt4
    advapi32
)

# set the final output name for the dll
set_target_properties(memoryreadingdll PROPERTIES OUTPUT_NAME "VERSION")

# link with the .def file
set_target_properties(memoryreadingdll PROPERTIES LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/VERSION.def\"")