# Implementation Plan: Writable attached roots

`PathSafety.TryResolveWrite` takes `(workspace, approval, path)`.

- `Ask`/`ReadOnly` delegate to `TryResolve`, preserving the primary-only path and every pinned message.
- `AllowAll` delegates absolute paths to `Workspace.TryResolveRead` (primary or attachment) and then to the existing
  physical `JudgePhysical(..., allowAttachments: true)`. Relative paths still use `TryResolve` and
  `JudgePhysical(..., allowAttachments: false)`.

The six write-capable tools call this resolver and use `PathSafety.Relative(workspace, owner, fullPath)` for output.
The file tools' descriptions now state the approval condition. No `Workspace` shape or public API changes; the
attachment owner was already returned by `TryResolveRead`.

## Tests

`ToolTests.WriteFile_WritesAnAttachmentOnlyWhenTheSessionAllowsAll` creates, replaces and refuses to delete under
`ReadOnly`; `PathSafetyTests.TheWritePathAllowsAnAttachmentOnlyUnderAllowAll` pins both modes. Existing refusal tests
remain unchanged.
