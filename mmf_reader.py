import ctypes
import time
import sys

# Windows API Constants
FILE_MAP_READ = 0x0004
INVALID_HANDLE_VALUE = -1 # Or ctypes.c_void_p(-1).value depending on definition

# Memory Mapped File Details
MMF_NAME = "NexusTKMemoryData"
MMF_SIZE = 1024 # 1KB

# Get necessary kernel32 functions
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)

OpenFileMappingA = kernel32.OpenFileMappingA
OpenFileMappingA.argtypes = [ctypes.c_uint32, ctypes.c_bool, ctypes.c_char_p]
OpenFileMappingA.restype = ctypes.c_void_p # HANDLE is typically represented as void*

MapViewOfFile = kernel32.MapViewOfFile
MapViewOfFile.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_size_t]
MapViewOfFile.restype = ctypes.c_void_p # LPVOID

UnmapViewOfFile = kernel32.UnmapViewOfFile
UnmapViewOfFile.argtypes = [ctypes.c_void_p]
UnmapViewOfFile.restype = ctypes.c_bool

CloseHandle = kernel32.CloseHandle
CloseHandle.argtypes = [ctypes.c_void_p]
CloseHandle.restype = ctypes.c_bool

def main():
    print(f"Attempting to open MMF: {MMF_NAME}")

    # Convert name to bytes for ctypes
    mmf_name_bytes = MMF_NAME.encode('ascii')

    # Open the existing file mapping object
    hMapFile = OpenFileMappingA(FILE_MAP_READ, False, mmf_name_bytes)

    if not hMapFile:
        print(f"Error opening file mapping object: {ctypes.get_last_error()}")
        print(f"Could not open MMF '{MMF_NAME}'. Make sure the DLL is running and has created it.")
        sys.exit(1)

    print(f"MMF opened successfully (Handle: {hMapFile}). Mapping view...")

    # Map a view of the file mapping object into the address space
    pBuf = MapViewOfFile(hMapFile, FILE_MAP_READ, 0, 0, MMF_SIZE)

    if not pBuf:
        print(f"Error mapping view of file: {ctypes.get_last_error()}")
        CloseHandle(hMapFile)
        sys.exit(1)

    print(f"View mapped successfully (Address: {pBuf}). Starting read loop (press Ctrl+C to exit)...")

    try:
        while True:
            try:
                # Read the raw bytes from the mapped memory
                # ctypes.string_at reads until the first null byte or up to MMF_SIZE
                raw_data = ctypes.string_at(pBuf, MMF_SIZE)

                # Find the first null terminator to get the actual content length
                null_term_pos = raw_data.find(b'\x00')
                if null_term_pos != -1:
                    actual_data = raw_data[:null_term_pos]
                else:
                    # Should not happen if C++ code ZeroMemory works, but handle defensively
                    actual_data = raw_data

                # Decode the bytes as UTF-8
                try:
                    content = actual_data.decode('utf-8')
                    print(f"[{time.strftime('%H:%M:%S')}] Content: {content}")
                except UnicodeDecodeError as e:
                    print(f"[{time.strftime('%H:%M:%S')}] Error decoding UTF-8: {e}")
                    print(f"Raw data (bytes): {actual_data!r}") # Print raw bytes if decode fails

            except Exception as e:
                print(f"Error reading from MMF: {e}")
                # Decide if the error is fatal or if we should try again
                time.sleep(1) # Wait before retrying after error

            # Wait for 1 second
            time.sleep(1)

    except KeyboardInterrupt:
        print("\nCtrl+C detected. Cleaning up and exiting.")
    finally:
        # Unmap the view and close the handle
        if pBuf:
            print("Unmapping view...")
            UnmapViewOfFile(pBuf)
        if hMapFile:
            print("Closing handle...")
            CloseHandle(hMapFile)
        print("Cleanup complete.")

if __name__ == "__main__":
    main() 