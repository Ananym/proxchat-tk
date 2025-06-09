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
    version.rc
)

# link with required windows libraries for named pipes
target_link_libraries(memoryreadingdll PRIVATE 
    kernel32 
    ws2_32 
    version
    advapi32
)

# set the final output name for the dll
set_target_properties(memoryreadingdll PROPERTIES OUTPUT_NAME "VERSION")

# link with the .def file
set_target_properties(memoryreadingdll PROPERTIES LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/VERSION.def\"")