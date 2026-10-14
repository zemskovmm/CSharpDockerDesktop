# IDD Intent Index

This index helps humans and Coding Agents find relevant current intent documents.
It is not the source of truth.

Current IDD-NNNN documents directly under this directory contain normative
product intent, ADRs, or active spikes.

GLOSSARY.md, when present, is an optional unnumbered vocabulary support file
and is not listed in this index.

Git history is the source for deleted or previous document versions.

Shared and cross-cutting requirements are discoverable through Area and Notes.
Implementation-only durable constraints belong in the optional Engineering
layer, not in this index.

## Current documents

The Document column contains stable IDD-NNNN identifiers only. Resolve each ID
to its unique current document when opening it.

| Document | Role | Area | Notes | Replaces |
| --- | --- | --- | --- | --- |
| IDD-0001 | Spec | Product scope | Windows developer workflow; explicit non-goals | — |
| IDD-0002 | Spec | Engine connection | Named pipe, API negotiation, diagnostics; TCP/TLS and discovery decisions unresolved | — |
| IDD-0003 | Spec | Shared UI / Inspect | Cross-cutting navigation, keyboard actions, confirmation and accessibility | — |
| IDD-0004 | Spec | Containers | Search, lifecycle, safe deletion and optional bulk scope | — |
| IDD-0005 | Spec | Images / Volumes / Networks | Metadata, dependencies, safe deletion; Pull/Push unresolved | — |
| IDD-0006 | Spec | Shared state synchronization | Authoritative snapshots, external events, reconnect and uncertain mutations | — |
| IDD-0007 | Spec | Logs | Bounded tail/follow, filtering, decoding and recovery; tuning unresolved | — |
| IDD-0008 | Spec | Terminal | Interactive TTY exec, input/output, resize and cancellation | — |
| IDD-0009 | Spec | Stats / Diagnostics | Active-view metrics, bounded history, health/exit/OOM/restarts | — |
| IDD-0010 | Spec | Shared security / Privacy | Privileged capability and sensitive-data prohibitions; telemetry unresolved | — |
| IDD-0011 | Spec | Shared responsiveness / Resources | Bounded work and performance targets; measurement baseline unresolved | — |
| IDD-0012 | ADR | Docker integration | Accepted custom-client rationale; implementation policy owned by Engineering | — |
| IDD-0013 | Spike | Terminal integration | Active terminal surface/licensing and duplex exec investigation | — |
| IDD-0014 | Spike | WSL compatibility | Active endpoint matrix / future bridge investigation | — |
