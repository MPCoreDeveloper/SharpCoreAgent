# Quickstart: Search latency harness

Run the ordinary suite; the harness is reported skipped:

```powershell
dotnet test tests/SharpCoreAgent.Tests/SharpCoreAgent.Tests.csproj -c Release
```

Run the measurement:

```powershell
$env:SHARPCOREAGENT_RUN_LATENCY = '1'
dotnet test tests/SharpCoreAgent.Tests/SharpCoreAgent.Tests.csproj -c Release --no-build `
  --filter 'FullyQualifiedName~SearchLatencyTests'
```

Expected shape:

```text
latency: files=1700 passages=20400 samples=100 p50=25.8 ms p95=50.8 ms max=903.0 ms
```

The build takes minutes because it writes 1,700 files; the 100 measured calls take seconds. The first sample includes
the cold snapshot load and is why the maximum is much larger than p95.
