import re

input_file = "dumpbin_iphlpapi_exports.txt"
output_file = "IPHLPAPI.def"
dll_name = "IPHLPAPI.DLL"
system_dll = "C:\\\\Windows\\\\SysWOW64\\\\IPHLPAPI.DLL"

# Exclude known data exports, DllMain, and any non-function lines
exclude_exports = {
    "do_echo_rep",
    "do_echo_req",
    "register_icmp",
    "functions",
    "names",
    "DllMain",
}

exports = []

with open(input_file, "r", encoding="utf-8") as f:
    for line in f:
        # Match lines like: "   264  123 0001F030 _PfUnBindInterface@4"
        match = re.match(r"\s*(\d+)\s+\w+\s+\w+\s+([@\w?]+)", line)
        if match:
            ordinal = int(match.group(1))
            func = match.group(2)
            # skip excluded and obviously invalid names
            if func not in exclude_exports and not func.startswith("??"):
                exports.append((func, ordinal))

with open(output_file, "w", encoding="utf-8") as f:
    f.write("LIBRARY IPHLPAPI\n")
    f.write("EXPORTS\n")
    for func, ordinal in exports:
        f.write(f'    {func} @{ordinal}="{system_dll}".{func}\n')

print(f"Generated {output_file} with {len(exports)} decorated forwarded exports.")
