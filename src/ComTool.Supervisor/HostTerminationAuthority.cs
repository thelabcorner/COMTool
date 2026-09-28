using ComTool.Hosts.Abstractions;

namespace ComTool.Supervisor;

/// <summary>
/// Refusal from the runtime-owned destructive host-lifecycle authority.
/// A refusal is always certified before any termination signal is issued.
/// </summary>
public sealed class HostTerminationAuthorityException(
    string kind,
    string message,
    bool reconciliationRequired = false,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Kind { get; } = kind;

    public bool ReconciliationRequired { get; } =
        reconciliationRequired;
}

/// <summary>
/// Authorizes destructive host termination only for a generation that the
/// durable launch ledger still marks <see cref="HostOwnershipRecordState.Owned"/>
/// and that the operating system still proves is the same PID/start-time/
/// executable generation. An attached or merely discovered process can never
/// acquire cleanup authority through this type.
/// </summary>
public sealed class HostTerminationAuthority
{
    private readonly HostOwnershipLedger _ledger;
    private readonly IHostProcessIdentityProbe _processProbe;

    public HostTerminationAuthority(
        HostOwnershipLedger ledger,
        IHostProcessIdentityProbe processProbe)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(processProbe);

        _ledger = ledger;
        _processProbe = processProbe;
    }

    public async ValueTask<HostOwnershipRecord> RequireOwnedGenerationAsync(
        HostTargetDescriptor target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        HostOwnershipRecord? ownership;
        try
        {
            ownership = _ledger.TryGetGeneration(
                target.Identity.TargetId);
        }
        catch (HostOwnershipLedgerException ex)
        {
            throw new HostTerminationAuthorityException(
                ex.Kind,
                "Host termination provenance could not be verified: " +
                ex.Message,
                reconciliationRequired: true,
                ex);
        }

        if (ownership is null)
        {
            throw new HostTerminationAuthorityException(
                "host_termination_not_owned",
                "The exact target generation has no durable launch ownership " +
                "record. Attached, discovered, and user-started host processes " +
                "are not eligible for runtime termination.");
        }

        if (ownership.State != HostOwnershipRecordState.Owned)
        {
            throw new HostTerminationAuthorityException(
                "host_termination_ownership_inactive",
                $"The exact target generation is recorded as " +
                $"'{ownership.State}' rather than owned.");
        }

        var observed = await _processProbe
            .TryReadAsync(
                target.Identity.ProcessId,
                cancellationToken)
            .ConfigureAwait(false);

        if (observed is null ||
            !observed.Matches(target.Identity))
        {
            throw new HostTerminationAuthorityException(
                "host_termination_generation_unproven",
                "The operating system no longer proves the exact " +
                "PID/start-time/executable generation recorded by the runtime. " +
                "Termination was refused before dispatch.",
                reconciliationRequired: true);
        }

        return ownership;
    }
}