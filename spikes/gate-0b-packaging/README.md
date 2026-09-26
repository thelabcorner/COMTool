# Gate 0B — Packaging and startup

This spike isolates executable distribution/startup cost from Illustrator COM latency.

Targets:

- Windows x64
- self-contained
- single-file
- no Native AOT
- no trimming for the initial correctness baseline

The gate records executable size plus repeated process-launch latency. A later optimization experiment may compare ReadyToRun/trimming only if it does not compromise dynamic COM or reflection-heavy transports.
