using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

/// <summary>UI-thread-owned draft and deliberate evidence reveal. All connection work belongs to Application.</summary>
public sealed class ConnectionEditorView(DesktopSession session)
{
    public const int MaximumAddressCharacters = 2048;
    public const int MaximumMessageCharacters = 16384;
    private ConnectionSnapshot? evidenceOwner;
    private string? evidenceMessage;
    public string Draft { get; set; } = session.Snapshot.Endpoint.Address;
    public ConnectionSnapshot Snapshot => session.Snapshot;
    public bool CanApply => Draft.Length is > 0 and <= MaximumAddressCharacters;
    public bool HasUnappliedDraft => Draft != Snapshot.Endpoint.Address;
    public string DraftStatus => !CanApply ? $"Enter an endpoint address (1–{MaximumAddressCharacters} characters)."
        : HasUnappliedDraft ? "Unapplied draft. Choose Apply / Connect to select this address. Retry uses the selected endpoint."
        : "Draft matches the selected endpoint. Retry reconnects this selection.";
    private bool showingDaemonMessage;
    public bool ShowingDaemonMessage => showingDaemonMessage && ReferenceEquals(evidenceOwner, Snapshot);
    public string DaemonMessage => ShowingDaemonMessage && evidenceMessage is { } value
        ? value.Length <= MaximumMessageCharacters ? value : value[..MaximumMessageCharacters] + "… [display truncated]" : "";
    public bool HasDaemonMessage => (Snapshot.Failure?.DaemonMessage ?? Snapshot.EventFailure?.DaemonMessage) is not null;

    public void Apply()
    {
        if (!CanApply) return;
        session.SelectEndpoint(EngineEndpointInput.FromAddress(Draft));
        Refresh();
    }
    public void UseDefault()
    {
        Draft = EngineEndpoint.LocalWindows.Address;
        session.SelectEndpoint(EngineEndpoint.LocalWindows);
        Refresh();
    }
    public void Retry() { session.Retry(); Refresh(); }
    public void HideDaemonMessage() { showingDaemonMessage = false; evidenceMessage = null; evidenceOwner = null; }
    public void Refresh()
    {
        var current = Snapshot;
        // Immutable snapshot identity also fences a missed intermediate Connecting notification,
        // an automatic reconnection and equal message text from different owners.
        if (!ReferenceEquals(evidenceOwner, current)) HideDaemonMessage();
    }
    public void RevealDaemonMessage()
    {
        evidenceOwner = Snapshot;
        evidenceMessage = evidenceOwner.Failure?.DaemonMessage ?? evidenceOwner.EventFailure?.DaemonMessage;
        showingDaemonMessage = evidenceMessage is not null;
    }
    public string Selection => Snapshot.Endpoint == EngineEndpoint.LocalWindows
        ? "Default local Windows pipe (explicit in-memory selection; no Docker CLI context selected)"
        : "Explicit in-memory endpoint (no Docker CLI context selected)";
    public string Diagnostic
    {
        get
        {
            var s = Snapshot;
            string endpoint = Preview(s.Endpoint.Address);
            string recovery = s.AutomaticRecoveryPending ? $" Automatic recovery is scheduled for {endpoint}."
                : s.State is EngineConnectionState.Unavailable or EngineConnectionState.Lost ? $" Use Retry connection for {endpoint} when ready." : "";
            return s.Failure is { } failure ? $"{FailureLabel(failure.Kind)}: {Preview(failure.Description, 4096)}{recovery}"
                : s.EventFailure is { } events ? $"Automatic resource updates interrupted ({events.Kind}): {Preview(events.Description, 4096)} "
                    + (s.EventRecoveryPending ? $"Observation recovery is scheduled for {endpoint}. " : $"Use Retry connection for {endpoint} to restore observation. ")
                    + "Manual refresh and resource navigation remain available."
                : s.State == EngineConnectionState.Connecting ? $"Connecting to {endpoint} ({s.Endpoint.Transport}). Navigation remains available."
                : s.State == EngineConnectionState.Connected ? "Engine connected. Browse Containers, Images, Volumes and Networks; inspect resources and deliberately confirm destructive actions. Automatic resource updates are active." : "";
        }
    }
    public string Details
    {
        get
        {
            var s = Snapshot; var m = s.Metadata; var c = m?.Capabilities;
            static string Flag(bool? value) => value switch { true => "available", false => "unavailable", _ => "unknown" };
            return $"Selection: {Selection}\nSelected endpoint: {Preview(s.Endpoint.Address)}\nSelected transport: {s.Endpoint.Transport}\nActual endpoint: {(m is null ? "unavailable (connection not established)" : Preview(m.Endpoint.Address))}\nActual transport: {(m is null ? "unavailable" : m.Endpoint.Transport.ToString())}\nEngine identity: {Preview(m?.EngineId)}\nEngine version: {Preview(m?.EngineVersion)}\nDaemon API: {m?.Api.DaemonMinimum.ToString() ?? "unknown"}–{m?.Api.DaemonMaximum.ToString() ?? "unknown"}\nNegotiated API: {m?.Api.Negotiated.ToString() ?? "unknown"}\nOperating system: {Preview(m?.OperatingSystem)}\nOS type: {Preview(m?.OSType)}\nRuntime OS: {Preview(m?.RuntimeOS)}\nHost architecture: {Preview(m?.HostArchitecture)}\nRuntime architecture: {Preview(m?.RuntimeArchitecture)}\nCapabilities reported by daemon:\nMemory limit: {Flag(c?.MemoryLimit)}; swap limit: {Flag(c?.SwapLimit)}\nCPU period: {Flag(c?.CpuCfsPeriod)}; CPU quota: {Flag(c?.CpuCfsQuota)}; CPU shares: {Flag(c?.CpuShares)}; CPU set: {Flag(c?.CpuSet)}\nPID limit: {Flag(c?.PidsLimit)}; IPv4 forwarding: {Flag(c?.IPv4Forwarding)}; experimental: {Flag(c?.Experimental)}\nSwarm state: {Preview(c?.SwarmLocalNodeState)}";
        }
    }
    public static string Preview(string? value, int maximum = MaximumAddressCharacters) => string.IsNullOrEmpty(value) ? "unavailable"
        : value.Length <= maximum ? value : value[..maximum] + "… [display truncated]";
    private static string FailureLabel(EngineConnectionFailureKind kind) => kind switch
    {
        EngineConnectionFailureKind.MissingEndpoint => "Pipe unavailable",
        EngineConnectionFailureKind.AccessDenied => "Access denied",
        EngineConnectionFailureKind.IncompatibleApi => "Incompatible Engine API",
        EngineConnectionFailureKind.UnsupportedEndpoint => "Unsupported endpoint or platform",
        EngineConnectionFailureKind.MalformedResponse => "Invalid Engine response",
        EngineConnectionFailureKind.DaemonHttpError => "Daemon error",
        EngineConnectionFailureKind.TransientTransportFailure => "Transport interrupted",
        _ => "Connection error"
    };
}
