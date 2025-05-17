cmake_minimum_required(VERSION 3.10)
project(memoryreadingdll CXX)

# Set C++ standard
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

# Add source files
add_library(memoryreadingdll SHARED
    dllmain.cpp
    memreader.cpp
)

# Link necessary Windows libraries
target_link_libraries(memoryreadingdll PRIVATE kernel32 ws2_32 iphlpapi)

# Set the final output name for the DLL
set_target_properties(memoryreadingdll PROPERTIES OUTPUT_NAME "IPHLPAPI")

# Ensure symbols are exported if needed (can also use .def file or __declspec)
# WINDOWS_EXPORT_ALL_SYMBOLS is convenient but can export more than needed.
# Consider using __declspec(dllexport) or a .def file for finer control.
# set_target_properties(memoryreadingdll PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS True)

# Optional: Set output directory for Release configuration
# set_target_properties(memoryreadingdll PROPERTIES RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_BINARY_DIR}/Release)

# Optional: Set output directory if desired
# set_target_properties(memoryreadingdll PROPERTIES RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR})

# set_target_properties(memoryreadingdll PROPERTIES LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/IPHLPAPI.def\"") 