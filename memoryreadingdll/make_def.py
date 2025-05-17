import re

input_file = "iphlpapi_exports.txt"
output_file = "IPHLPAPI.def"
dll_path = "IPHLPAPI.DLL"

with open(input_file, "r", encoding="utf-8") as f:
    lines = f.readlines()

exports = []
for i, line in enumerate(lines):
    match = re.match(r"\s*Name\s*:\s*(\w+)", line)
    if match:
        func = match.group(1)
        exports.append(func)

# List of known data exports to skip (add more if needed)
data_exports = {"do_echo_rep", "do_echo_req", "register_icmp"}

with open(output_file, "w", encoding="utf-8") as f:
    f.write("LIBRARY IPHLPAPI\n")
    f.write("EXPORTS\n")
    for func in exports:
        if func in data_exports:
            continue  # skip data exports
        f.write(f"    {func} = {dll_path}.{func}\n")

print(f"Generated {output_file} with {len(exports)} forwarded exports.")
