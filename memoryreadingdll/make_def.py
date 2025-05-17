import re

input_file = "iphlpapi_exports.txt"
output_file = "IPHLPAPI.def"
dll_name = "IPHLPAPI.DLL"

# List of known data exports to skip (add more if needed)
data_exports = {"do_echo_rep", "do_echo_req", "register_icmp"}

exports = []
ordinal = 1
func = None

with open(input_file, "r", encoding="utf-8") as f:
    for line in f:
        name_match = re.match(r"\s*Name\s*:\s*(\w+)", line)
        if name_match:
            func = name_match.group(1)
            if func not in data_exports:
                exports.append((func, ordinal))
            ordinal += 1

if not exports:
    print("No exports found! Please check the format of iphlpapi_exports.txt.")

with open(output_file, "w", encoding="utf-8") as f:
    f.write("LIBRARY IPHLPAPI\n")
    f.write("EXPORTS\n")
    for func, ordinal in exports:
        f.write(f"    {func} @{ordinal}={dll_name}.{func}\n")

print(f"Generated {output_file} with {len(exports)} forwarded exports (with ordinals).")
