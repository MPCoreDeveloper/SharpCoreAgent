# Quickstart: Python and Node verification

Both tools are registered by default, beside `dotnet_verify`.

```powershell
# Discover and run Python unittest from the workspace root.
sharpcoreagent run "fix the cart total and verify it"

# The model can narrow a run:
#   python_verify { "target": "tests" }
#   python_verify { "target": "tests/test_cart.py" }
#   node_verify   { "target": "test/discount.test.js" }
```

The tools run:

```text
python -m unittest discover -v
node --test
```

A passing result closes the verification gap for its profile. `run_shell` can run the same tests for diagnosis, but it
does not close the gate. Targets outside the workspace are refused; a missing runtime is an error that names the
executable.

The recorded evaluation covers both tools:

```powershell
dotnet run --project eng/SharpCoreAgent.Evaluation -c Release -- --check
```

Expected: 3/3, 61,573 input / 249 output tokens, 0 unintended changes. See
[`contracts/runtime-verifiers.md`](contracts/runtime-verifiers.md) for the exact schemas.
