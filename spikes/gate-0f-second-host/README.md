# Gate 0F — Second-host falsification spike

Reference second host: **Adobe Photoshop 2026**.

This gate exists to detect architecture that is accidentally Illustrator-shaped.

The spike:

1. resolves `Photoshop.Application`,
2. checks whether an active instance already exists,
3. attaches if it does,
4. otherwise launches a COM instance and marks it **owned by the spike**,
5. reads generic host identity/state,
6. probes script capability independently,
7. never creates or mutates a document,
8. quits Photoshop only when the spike launched it **and** it still has zero documents.

A host capability is allowed to be unsupported. The gate tests truthful capability discovery and host isolation, not Illustrator parity.
