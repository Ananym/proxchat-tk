# Build Prerequisites for memoryreadingdll

To build the `memoryreadingdll.dll` project, you need the following software installed:

1.  **Microsoft C++ Build Tools:**
    *   Provides the MSVC compiler, linker, and Windows SDK necessary for building the C++ code.
    *   **Download:** Get the Visual Studio Installer from [https://visualstudio.microsoft.com/downloads/](https://visualstudio.microsoft.com/downloads/).
    *   **Installation:** Run the installer and select the **"Desktop development with C++"** workload. You do not need the full Visual Studio IDE unless you want it; the build tools are sufficient.

2.  **CMake:**
    *   A cross-platform build system generator used to configure the project and generate build files (e.g., Visual Studio solution files).
    *   **Download:** Get the installer from the official CMake website: [https://cmake.org/download/](https://cmake.org/download/).
    *   **Installation:** During installation, ensure you select the option to **"Add CMake to the system PATH"** for ease of use from the command line.

Once both are installed, you should be able to follow the build instructions using the provided `CMakeLists.txt` file. 