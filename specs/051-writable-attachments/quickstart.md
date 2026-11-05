# Quickstart: Writable attached roots

```powershell
sharpcoreagent chat --add-root D:\repos\Shared.Contracts --approval allow
```

Then an absolute path inside the attached root is writable:

```text
write_file {"path":"D:\\repos\\Shared.Contracts\\src\\Order.cs","content":"..."}
```

Without `--approval allow`, the same call is refused with `read-only attachment; writes stay in the primary root`.
Relative paths still resolve against the primary workspace. `list_roots` prints the absolute prefixes.
