using System.IO.Pipes;
using System.Net;
using System.Text;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

const string Info = """{"ID":"engine-id","Name":"engine-name","OperatingSystem":"Alpine Linux v3.5","OSType":"linux","Architecture":"x86_64","ServerVersion":"19.03.12","MemoryLimit":true,"SwapLimit":false,"CpuCfsPeriod":true,"CpuCfsQuota":true,"CPUShares":true,"CPUSet":true,"PidsLimit":true,"IPv4Forwarding":true,"ExperimentalBuild":false,"Swarm":{"LocalNodeState":"inactive"}}""";

// Dependency-free executable test suite: every failed assertion exits nonzero.
// It intentionally builds without Uno or an installed/running Docker daemon.
if (args.FirstOrDefault() == "--smoke")
{
    var endpoint = args.Length > 1 ? new EngineEndpoint(args[1], EngineTransport.WindowsNamedPipe) : null;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var result = await new DockerEngineConnectionClient().ConnectAsync(endpoint, stop.Token);
    if (result.Connection is { } connection)
    {
        await using (connection)
        {
            var m = connection.Metadata;
            Console.WriteLine($"Connected: {m.Endpoint.Address} ({m.Endpoint.Transport}); Engine {m.EngineVersion}; daemon API {m.Api.DaemonMinimum}-{m.Api.DaemonMaximum}; negotiated {m.Api.Negotiated}; OS {m.OSType ?? m.RuntimeOS ?? "unreported"}; host architecture {m.HostArchitecture ?? "unreported"}.");
        }
        return 0;
    }
    Console.WriteLine($"{result.Failure!.Kind}: {result.Failure.Description} HTTP status: {result.Failure.HttpStatusCode}");
    // Raw daemon content is deliberately not printed automatically.
    return 2;
}

var tests = new (string, Func<Task>)[]
{
    ("Exec resize official negotiated contracts and dimension ordering", ExecResizeChecks.OfficialContractsAndDimensions),
    ("Exec resize validation binding errors and bounded private evidence", ExecResizeChecks.ValidationBindingAndErrors),
    ("Exec resize shared admission cancellation deadline and continuity", ExecResizeChecks.AdmissionAndCooperativeLifetimes),
    ("Exec resize late acknowledgement rejection and joined disposal", ExecResizeChecks.LateEvidenceAndJoinedDisposal),
    ("Exec resize named-pipe outcomes and no replay", ExecResizeChecks.NamedPipeOutcomesAndNoReplay),
    ("Exec attachment official contracts raw duplex and EOF", ExecAttachmentChecks.ContractsAndRawDuplex),
    ("Exec attachment rejections framing and continuity", ExecAttachmentChecks.RejectionsFramingAndContinuity),
    ("Exec attachment cancellation admission and ownership", ExecAttachmentChecks.CancellationAdmissionAndOwnership),
    ("Exec attachment noncooperative cleanup and input", ExecAttachmentChecks.NoncooperativeCleanupAndInput),
    ("Exec attachment named-pipe duplex lost response and no replay", ExecAttachmentChecks.NamedPipeDuplexAndLostStart),
    ("Exec official negotiated contracts and explicit argument serialization", ExecChecks.OfficialContractsAndArguments),
    ("Exec additive unavailable and malformed evidence", ExecChecks.MissingNullAndMalformedEvidence),
    ("Exec bounded identities arguments request response and error bodies", ExecChecks.InputsAndBounds),
    ("Exec classified failures and no automatic replay", ExecChecks.ClassifiedFailures),
    ("Exec cancellation deadlines shared admission and generation", ExecChecks.CooperativeLifetimesAndAdmission),
    ("Exec late evidence stale results and joined disposal", ExecChecks.LateEvidenceAndJoinedDisposal),
    ("Exec historical acknowledgement rejection and shared admission", ExecChecks.HistoricalEvidenceAndSharedAdmission),
    ("Exec named-pipe creation uncertainty cancellation and no replay", ExecChecks.NamedPipeCreationOutcomes),
    ("Stats presentation ownership and detail tabs", StatsPresentationChecks.OwnershipAndTabs),
    ("Stats presentation slow cleanup and bounded replacements", StatsPresentationChecks.SlowCleanupAndReplacement),
    ("Stats presentation recreated page work budgets", StatsPresentationChecks.PageRecreationBudgets),
    ("Stats presentation obsolete reads and queued callbacks", StatsPresentationChecks.ObsoleteReadsAndCallbacks),
    ("Stats presentation coherent revisions eviction bounds and sustained updates", StatsPresentationChecks.RevisionsEvictionAndBounds),
    ("Stats presentation lifecycle recovery and private evidence", StatsPresentationChecks.LifecycleRecoveryAndPrivacy),
    ("Stats presentation valid zeros availability and memory provenance", StatsPresentationChecks.ValuesAndProvenance),
    ("Stats presentation chart continuity and contexts", StatsPresentationChecks.ChartContinuity),
    ("Stats session admission cadence zeros and bounds", StatsSessionChecks.AdmissionCadenceZerosAndBounds),
    ("Stats session replacements late open and joined close", StatsSessionChecks.ReplacementsLateOpenAndJoinedClose),
    ("Stats session selection disconnect disposal and late failures", StatsSessionChecks.SelectionDisconnectDisposalAndLateFailures),
    ("Stats session lifecycle authority and stale publications", StatsSessionChecks.LifecycleAuthorityAndStalePublications),
    ("Stats session late opening lifecycle and inspection fences", StatsSessionChecks.LateOpeningLifecycleAndInspectionFences),
    ("Stats session stalled empty completed and permanent failures", StatsSessionChecks.StalledEmptyCompletedAndPermanentFailures),
    ("Stats session recovery context reset limits and close", StatsSessionChecks.RecoveryContextResetLimitsAndClose),
    ("Stats session opening deadline retention and coexistence", StatsSessionChecks.OpeningDeadlineRetentionAndCoexistence),
    ("Stats calculation CPU and exact time", StatsCalculationChecks.CpuAndExactTime),
    ("Stats calculation memory and Windows", StatsCalculationChecks.MemoryAndWindows),
    ("Stats calculation baselines and bounded state", StatsCalculationChecks.BaselinesAndBoundedState),
    ("Stats calculation topology and integer boundaries", StatsCalculationChecks.TopologyAndIntegerBoundaries),
    ("Stats official negotiated contracts and platform evidence", StatsChecks.OfficialContracts),
    ("Stats decoder fragmentation types and bounds", StatsChecks.DecoderBoundsAndMalformed),
    ("Stats opening failures and deadlines", StatsChecks.OpeningFailures),
    ("Stats admission and blocked delivery", StatsChecks.AdmissionAndBlockedDelivery),
    ("Stats cancellation and joined ownership", StatsChecks.CancellationAndJoinedOwnership),
    ("Stats established stream and failure completion", StatsChecks.EstablishedStreamsAndFailures),
    ("Stats cleanup reader race", () => StreamTerminationChecks.ReaderCrossesCleanup(false, true)),
    ("Stats cleanup cancellation and replacement race", () => StreamTerminationChecks.CancellationAndReplacement(false, true)),
    ("Network operations NetworkDeletionAtomicAdmissionAndReplay", NetworkBrowsingChecks.NetworkDeletionAtomicAdmissionAndReplay),
    ("Network operations NetworkDeletionEligibilityAndStaleness", NetworkBrowsingChecks.NetworkDeletionEligibilityAndStaleness),
    ("Network operations NetworkDeletionExactIdentityAndSelection", NetworkBrowsingChecks.NetworkDeletionExactIdentityAndSelection),
    ("Network operations NetworkDeletionProtocolOutcomesAndCancellation", NetworkBrowsingChecks.NetworkDeletionProtocolOutcomesAndCancellation),
    ("Network operations NetworkDeletionConflictAttachments", NetworkBrowsingChecks.NetworkDeletionConflictAttachments),
    ("Network operations NetworkDeletionContradictoryAndFailedObservations", NetworkBrowsingChecks.NetworkDeletionContradictoryAndFailedObservations),
    ("Network operations NetworkDeletionReadFencesAndEvents", NetworkBrowsingChecks.NetworkDeletionReadFencesAndEvents),
    ("Network operations NetworkDeletionConflictEventAndTransportFences", NetworkBrowsingChecks.NetworkDeletionConflictEventAndTransportFences),
    ("Network operations NetworkDeletionRecoveryIdentity", NetworkBrowsingChecks.NetworkDeletionRecoveryIdentity),
    ("Network operations NetworkDeletionCommandDeadlinesAndLateEvidence", NetworkBrowsingChecks.NetworkDeletionCommandDeadlinesAndLateEvidence),
    ("Network operations NetworkDeletionNonCooperativeReadsAndTeardown", NetworkBrowsingChecks.NetworkDeletionNonCooperativeReadsAndTeardown),
    ("Network operations NetworkDeletionRetryJoinsCommandAndPreparation", NetworkBrowsingChecks.NetworkDeletionRetryJoinsCommandAndPreparation),
    ("Network operations NetworkDeletionCoexistenceAndLateCallerResponse", NetworkBrowsingChecks.NetworkDeletionCoexistenceAndLateCallerResponse),
    ("Network preparation NetworkPreparationEvidenceAndEligibility", NetworkBrowsingChecks.NetworkPreparationEvidenceAndEligibility),
    ("Network preparation NetworkPreparationAdmissionFreshnessAndSelection", NetworkBrowsingChecks.NetworkPreparationAdmissionFreshnessAndSelection),
    ("Network preparation NetworkPreparationEventFencesAndGaps", NetworkBrowsingChecks.NetworkPreparationEventFencesAndGaps),
    ("Network preparation NetworkPreparationDisappearanceAndFailures", NetworkBrowsingChecks.NetworkPreparationDisappearanceAndFailures),
    ("Network preparation NetworkPreparationCapacityCancellationAndJoin", NetworkBrowsingChecks.NetworkPreparationCapacityCancellationAndJoin),
    ("Network preparation NetworkPreparationDeadlineRecoveryAndReconnection", NetworkBrowsingChecks.NetworkPreparationDeadlineRecoveryAndReconnection),
    ("Network preparation NetworkPreparationCoexistence", NetworkBrowsingChecks.NetworkPreparationCoexistence),
    ("Application Networks NetworkEventFailureContentAndObsoleteFailures", NetworkBrowsingChecks.NetworkEventFailureContentAndObsoleteFailures),
    ("Application Networks NetworkMissingCapabilityKeepsSharedObservation", NetworkBrowsingChecks.NetworkMissingCapabilityKeepsSharedObservation),
    ("Application Networks NetworkStartupBridgeWithoutConcurrentObservation", NetworkBrowsingChecks.NetworkStartupBridgeWithoutConcurrentObservation),
    ("Application Networks NetworkActorIdentityAndConservativeDetail", NetworkBrowsingChecks.NetworkActorIdentityAndConservativeDetail),
    ("Application Networks NetworkSnapshotObservationBoundaries", NetworkBrowsingChecks.NetworkSnapshotObservationBoundaries),
    ("Application Networks NetworkSelectedObservationFences", NetworkBrowsingChecks.NetworkSelectedObservationFences),
    ("Application Networks NetworkBurstCoalescingAndFairness", NetworkBrowsingChecks.NetworkBurstCoalescingAndFairness),
    ("Application Networks NetworkObservationFailureAndReopening", NetworkBrowsingChecks.NetworkObservationFailureAndReopening),
    ("Application Networks NetworkObservationOpeningFallbackAndJoin", NetworkBrowsingChecks.NetworkObservationOpeningFallbackAndJoin),
    ("Application Networks NetworkReconnectOverlapAndJoinedReplacement", NetworkBrowsingChecks.NetworkReconnectOverlapAndJoinedReplacement),
    ("Application Networks NetworkObservationsCoexistWithCommandsAndLogs", NetworkBrowsingChecks.NetworkObservationsCoexistWithCommandsAndLogs),
    ("Application Networks NetworkInvalidatedDeadlineAndReplacementCleanup", NetworkBrowsingChecks.NetworkInvalidatedDeadlineAndReplacementCleanup),
    ("Application Networks ImmutableEvidenceAndExactIdentity", NetworkBrowsingChecks.ImmutableEvidenceAndExactIdentity),
    ("Application Networks EmptyUnavailableAndDisappearance", NetworkBrowsingChecks.EmptyUnavailableAndDisappearance),
    ("Application Networks CancellationCallbacksSeeInvalidatedState", NetworkBrowsingChecks.CancellationCallbacksSeeInvalidatedState),
    ("Application Networks SuccessfulBrowsingCoexistsWithOwnedCapabilities", NetworkBrowsingChecks.SuccessfulBrowsingCoexistsWithOwnedCapabilities),
    ("Application Networks ClassifiedFailuresAndReturnedIdentity", NetworkBrowsingChecks.ClassifiedFailuresAndReturnedIdentity),
    ("Application Networks SelectionRefreshFencesAndFairness", NetworkBrowsingChecks.SelectionRefreshFencesAndFairness),
    ("Application Networks ReconnectAndJoinedReplacement", NetworkBrowsingChecks.ReconnectAndJoinedReplacement),
    ("Application Networks RefreshBurstFencesLateListFailures", NetworkBrowsingChecks.RefreshBurstFencesLateListFailures),
    ("Application Networks DeadlinesIncludingSupersededNonCooperativeReads", NetworkBrowsingChecks.DeadlinesIncludingSupersededNonCooperativeReads),
    ("Application Networks NetworkLossUsesSharedRecovery", NetworkBrowsingChecks.NetworkLossUsesSharedRecovery),
    ("Application Networks RepeatedDisconnectAndDisposalJoin", NetworkBrowsingChecks.RepeatedDisconnectAndDisposalJoin),
    ("Application Networks EverySharedOwnerInvalidatesNetworks", NetworkBrowsingChecks.EverySharedOwnerInvalidatesNetworks),
    ("Application Networks NetworkDeadlineRecoveryAndCapabilityCoexistence", NetworkBrowsingChecks.NetworkDeadlineRecoveryAndCapabilityCoexistence),


    ("Network deletion OfficialContracts", NetworkCommandChecks.OfficialContracts),
    ("Network deletion IdentitiesAndFailures", NetworkCommandChecks.IdentitiesAndFailures),
    ("Network deletion ErrorsAndBounds", NetworkCommandChecks.ErrorsAndBounds),
    ("Network deletion LifetimesAndSharedAdmission", NetworkCommandChecks.LifetimesAndSharedAdmission),
    ("Network deletion LateResponseFences", NetworkCommandChecks.LateResponseFences),
    ("Network deletion NamedPipeNoReplay", NetworkCommandChecks.NamedPipeNoReplay),
    ("Network deletion NamedPipeIssuedLifetimes", NetworkCommandChecks.NamedPipeIssuedLifetimes),

    ("Volume deletion presentation LoadingCancellationAndExactCapture", VolumeOperationPresentationChecks.LoadingCancellationAndExactCapture),
    ("Volume deletion presentation CompleteRunningStoppedAndFullCopy", VolumeOperationPresentationChecks.CompleteRunningStoppedAndFullCopy),
    ("Volume deletion presentation IncompleteEmptyUnavailableAndFailed", VolumeOperationPresentationChecks.IncompleteEmptyUnavailableAndFailed),
    ("Volume deletion presentation SingleSubmissionAndSessionOwnership", VolumeOperationPresentationChecks.SingleSubmissionAndSessionOwnership),
    ("Volume deletion presentation SelectionChangesNeverRetarget", VolumeOperationPresentationChecks.SelectionChangesNeverRetarget),
    ("Volume deletion presentation LiveInvalidationBeforeQueuedNotification", VolumeOperationPresentationChecks.LiveInvalidationBeforeQueuedNotification),
    ("Volume deletion presentation ResourceGenerationAndDetailFences", VolumeOperationPresentationChecks.ResourceGenerationAndDetailFences),
    ("Volume deletion presentation NavigationDuringSlowPreparationAndCleanupJoin", VolumeOperationPresentationChecks.NavigationDuringSlowPreparationAndCleanupJoin),
    ("Volume deletion presentation CloseDetailReattachAndQueuedWorkFences", VolumeOperationPresentationChecks.CloseDetailReattachAndQueuedWorkFences),
    ("Volume deletion presentation RetainedOperationAfterReopening", VolumeOperationPresentationChecks.RetainedOperationAfterReopening),
    ("Volume deletion presentation ConflictRefreshNeverAuthorizesRetry", VolumeOperationPresentationChecks.ConflictRefreshNeverAuthorizesRetry),
    ("Volume deletion presentation OriginalEvidenceHideReplacementAndStaleCopy", VolumeOperationPresentationChecks.OriginalEvidenceHideReplacementAndStaleCopy),
    ("Volume deletion presentation ClassifiedRejectionInterruptionAndNameRecreation", VolumeOperationPresentationChecks.ClassifiedRejectionInterruptionAndNameRecreation),
    ("Volume deletion presentation LargeDependenciesAndInspectCoexistence", VolumeOperationPresentationChecks.LargeDependenciesAndInspectCoexistence),
    ("Volume deletion presentation OffThreadCoalescingAndExternalPendingAdmission", VolumeOperationPresentationChecks.OffThreadCoalescingAndExternalPendingAdmission),

    ("Application volume DeletionPreparationAdmissionAndReplay", VolumeBrowsingChecks.DeletionPreparationAdmissionAndReplay),
    ("Application volume DeletionStaleInvalidatedAndUnavailablePreparations", VolumeBrowsingChecks.DeletionStaleInvalidatedAndUnavailablePreparations),
    ("Application volume DeletionExactIncompleteEvidenceAndSelectionIndependence", VolumeBrowsingChecks.DeletionExactIncompleteEvidenceAndSelectionIndependence),
    ("Application volume DeletionCommandDispositionsAndObservations", VolumeBrowsingChecks.DeletionCommandDispositionsAndObservations),
    ("Application volume DeletionIncompleteListsAndExactNotFound", VolumeBrowsingChecks.DeletionIncompleteListsAndExactNotFound),
    ("Application volume DeletionCallerCancellationEvidence", VolumeBrowsingChecks.DeletionCallerCancellationEvidence),
    ("Application volume DeletionConflictRefresh", VolumeBrowsingChecks.DeletionConflictRefresh),
    ("Application volume DeletionConflictFailureAndDeadline", VolumeBrowsingChecks.DeletionConflictFailureAndDeadline),
    ("Application volume DeletionPreCommandAndEventReadFences", VolumeBrowsingChecks.DeletionPreCommandAndEventReadFences),
    ("Application volume DeletionPreResponseAndRecreationObservations", VolumeBrowsingChecks.DeletionPreResponseAndRecreationObservations),
    ("Application volume DeletionResponseLossAndRecoveryIdentity", VolumeBrowsingChecks.DeletionResponseLossAndRecoveryIdentity),
    ("Application volume DeletionCommandDeadlineAndLateTeardown", VolumeBrowsingChecks.DeletionCommandDeadlineAndLateTeardown),
    ("Application volume DeletionNonCooperativeObservationAndRetry", VolumeBrowsingChecks.DeletionNonCooperativeObservationAndRetry),
    ("Application volume DeletionCommandRetryAndCapabilityCoexistence", VolumeBrowsingChecks.DeletionCommandRetryAndCapabilityCoexistence),
    ("Application volume DeletionAtomicConsumptionAndMissingCapabilities", VolumeBrowsingChecks.DeletionAtomicConsumptionAndMissingCapabilities),
    ("Application volume DeletionRecoveryEndpointAndUnknownOrigin", VolumeBrowsingChecks.DeletionRecoveryEndpointAndUnknownOrigin),
    ("Application volume DeletionDeadlineCancellationAndLateCallerResponse", VolumeBrowsingChecks.DeletionDeadlineCancellationAndLateCallerResponse),
    ("Application volume DeletionConflictEventFenceAndTransportEvidence", VolumeBrowsingChecks.DeletionConflictEventFenceAndTransportEvidence),
    ("Application volume DeletionNonCooperativeListDeadline", VolumeBrowsingChecks.DeletionNonCooperativeListDeadline),
    ("Volume preparation immutable exact running/stopped and missing evidence", VolumeBrowsingChecks.PreparationEvidence),
    ("Volume preparation bounded replacement, cancellation and non-cooperative joins", VolumeBrowsingChecks.PreparationReplacement),
    ("Volume preparation event fences before/during/after acquisition", VolumeBrowsingChecks.PreparationEvents),
    ("Volume preparation authoritative disappearance and selection independence", VolumeBrowsingChecks.PreparationDisappearance),
    ("Volume preparation classified failures and shared transport recovery", VolumeBrowsingChecks.PreparationFailuresAndRecovery),
    ("Volume preparation whole deadline, generation and joined teardown", VolumeBrowsingChecks.PreparationDeadlineAndTeardown),
    ("Volume preparation missing capabilities and retained stream coexistence", VolumeBrowsingChecks.PreparationCoexistence),
    ("Volume dependencies API 1.40–1.56 contracts and exact running/stopped mounts", VolumeDependencyChecks.ContractsAndExactMatching),
    ("Volume dependencies original names, encoding and evidence completeness", VolumeDependencyChecks.NamesAndEvidenceStates),
    ("Volume dependencies classified failures, malformed evidence and bounds", VolumeDependencyChecks.FailuresAndBounds),
    ("Volume dependencies cancellation, bounded admission and late results", VolumeDependencyChecks.CancellationAdmissionAndLateFences),
    ("Volume dependencies continuity fences and retained capability coexistence", VolumeDependencyChecks.ContinuityAndResourceCoexistence),
    ("Application Volumes startup bridge without a concurrent observation", VolumeBrowsingChecks.VolumeStartupBridgeWithoutConcurrentObservation),
    ("Application Volumes exact actor identity and conservative requested detail", VolumeBrowsingChecks.VolumeActorIdentityAndConservativeDetail),
    ("Application Volumes event/list evidence and obsolete failure fences", VolumeBrowsingChecks.VolumeEventListingEvidenceAndObsoleteFailures),
    ("Application Volumes shared observation startup and snapshot boundaries", VolumeBrowsingChecks.VolumeSnapshotObservationBoundaries),
    ("Application Volumes selected observations fence obsolete Inspect and disappearance", VolumeBrowsingChecks.VolumeSelectedObservationFences),
    ("Application Volumes bounded duplicate bursts and list/detail fairness", VolumeBrowsingChecks.VolumeBurstCoalescingAndFairness),
    ("Application Volumes observation failure, manual browsing and reopening", VolumeBrowsingChecks.VolumeObservationFailureAndReopening),
    ("Application Volumes non-cooperative observation opening fallback and join", VolumeBrowsingChecks.VolumeObservationOpeningFallbackAndJoin),
    ("Application Volumes precise reconnect overlap and generation replacement", VolumeBrowsingChecks.VolumeReconnectOverlapAndJoinedReplacement),
    ("Application Volumes observation coexistence with resources, commands and Logs", VolumeBrowsingChecks.VolumeObservationsCoexistWithCommandsAndLogs),
    ("Application Volumes event-invalidated deadlines and joined cleanup", VolumeBrowsingChecks.VolumeInvalidatedDeadlineAndReplacementCleanup),

    ("Application Volumes immutable metadata and exact inspection", VolumeBrowsingChecks.InitialMetadataAndImmutableState),
    ("Application Volumes partial/unavailable listing and warnings", VolumeBrowsingChecks.PartialUnavailableWarningsAndDisappearance),
    ("Application Volumes null/empty and missing capability", VolumeBrowsingChecks.InitialNullEmptyAndMissingCapability),
    ("Application Volumes classified failures and returned identity", VolumeBrowsingChecks.ClassifiedFailuresAndReturnedIdentity),
    ("Application Volumes burst bounds, fairness and stale fences", VolumeBrowsingChecks.SelectionRefreshFencesAndFairness),
    ("Application Volumes reconnect and joined replacement", VolumeBrowsingChecks.ReconnectAndJoinedReplacement),
    ("Application Volumes refresh bursts fence late list failures", VolumeBrowsingChecks.RefreshBurstFencesLateListFailures),
    ("Application Volumes deadlines and superseded non-cooperative reads", VolumeBrowsingChecks.DeadlinesIncludingSupersededNonCooperativeReads),
    ("Application Volumes transport loss uses shared recovery", VolumeBrowsingChecks.VolumeLossUsesSharedRecovery),
    ("Application Volumes repeated disconnect/disposal joins", VolumeBrowsingChecks.RepeatedDisconnectAndDisposalJoin),
    ("Application Volumes every shared owner invalidates state", VolumeBrowsingChecks.EverySharedOwnerInvalidatesVolumes),
    ("Application Volumes shared deadline recovery and capability coexistence", VolumeBrowsingChecks.VolumeDeadlineRecoveryAndCapabilityCoexistence),
    ("Networks negotiated API 1.40–1.56 schema contracts", NetworkChecks.ContractFixtures),
    ("Networks immutable nullable projections and exact raw inspection", NetworkChecks.ProjectionsAndRaw),
    ("Networks complete identities, escaping and classified failures", NetworkChecks.IdentitiesAndFailures),
    ("Networks malformed fields, identities and response budgets", NetworkChecks.MalformedAndBounds),
    ("Networks cancellation, deadlines and connection disposal", NetworkChecks.CancellationAndDisposal),
    ("Networks retained transport continuity fences", NetworkChecks.Continuity),
    ("Networks late responses and decoder publication fences", NetworkChecks.LateResponses),
    ("Volumes negotiated API 1.40–1.56 contract fixtures", VolumeChecks.ContractFixtures),
    ("Volumes immutable metadata, warnings and exact raw Inspect", VolumeChecks.ProjectionAndWarnings),
    ("Volumes complete safe names and classified failures", VolumeChecks.ErrorsAndNames),
    ("Volumes malformed responses and bounded success/error bodies", VolumeChecks.MalformedAndBounds),
    ("Volumes caller/header/body cancellation, deadlines and disposal", VolumeChecks.CancellationAndDisposal),
    ("Volumes retained connection continuity fences", VolumeChecks.Continuity),
    ("Volumes late header/body results and decoder publication fences", VolumeChecks.LateResponses),
    ("Application Image Delete DeadlineCancellationEvidence", ImageBrowsingChecks.ImageDeleteDeadlineCancellationEvidence),
    ("Application Image Delete PreResponseReadFenceAndNotFoundInspect", ImageBrowsingChecks.ImageDeletePreResponseReadFenceAndNotFoundInspect),
    ("Application Image Delete JoinedRetryAndCoexistence", ImageBrowsingChecks.ImageDeleteJoinedRetryAndCoexistence),
    ("Application Image Delete AcknowledgedAndParents", ImageBrowsingChecks.ImageDeleteAcknowledgedAndParents),
    ("Application Image Delete ConflictUntagAndEmpty", ImageBrowsingChecks.ImageDeleteConflictUntagAndEmpty),
    ("Application Image Delete AdmissionBounds", ImageBrowsingChecks.ImageDeleteAdmissionBounds),
    ("Application Image Delete DeselectionAndCallerCancellation", ImageBrowsingChecks.ImageDeleteDeselectionAndCallerCancellation),
    ("Application Image Delete ReadAndEventFences", ImageBrowsingChecks.ImageDeleteReadAndEventFences),
    ("Application Image Delete ResponseLossRecoveryAndEngineFence", ImageBrowsingChecks.ImageDeleteResponseLossRecoveryAndEngineFence),
    ("Application Image Delete BoundedFailedObservations", ImageBrowsingChecks.ImageDeleteBoundedFailedObservations),
    ("Application Image Delete DeadlinesAndLateShutdown", ImageBrowsingChecks.ImageDeleteDeadlinesAndLateShutdown),
    ("Application Image Delete DisconnectJoins", ImageBrowsingChecks.ImageDeleteDisconnectJoins),

    ("Application Images observation coexistence with containers, commands and Logs", ImageBrowsingChecks.ImageObservationsCoexistWithCommandsAndLogs),
    ("Application Images event-invalidated deadline and joined replacement cleanup", ImageBrowsingChecks.ImageInvalidatedDeadlineAndReplacementCleanup),
    ("Application Images observation startup and snapshot boundaries", ImageBrowsingChecks.ImageSnapshotObservationBoundaries),
    ("Application Images selected observations fence stale Inspect and disappearance", ImageBrowsingChecks.ImageSelectedObservationFences),
    ("Application Images bounded duplicate bursts and list/detail fairness", ImageBrowsingChecks.ImageBurstCoalescingAndFairness),
    ("Application Images observation failure, manual browsing and reopening", ImageBrowsingChecks.ImageObservationFailureAndReopening),
    ("Application Images non-cooperative observation opening fallback and join", ImageBrowsingChecks.ImageObservationOpeningFallbackAndJoin),
    ("Application Images exact reconnect overlap and joined generation replacement", ImageBrowsingChecks.ImageReconnectOverlapAndJoinedReplacement),
    ("Application Images MissingCapabilityAndFairness", ImageBrowsingChecks.MissingCapabilityAndFairness),
    ("Application Images NonCooperativeDeadlineAndSharedRecovery", ImageBrowsingChecks.NonCooperativeDeadlineAndSharedRecovery),
    ("Application Images SharedInvalidationAndJoinedShutdown", ImageBrowsingChecks.SharedInvalidationAndJoinedShutdown),
    ("Application Images InitialEmptyAndReplacement", ImageBrowsingChecks.InitialEmptyAndReplacement),
    ("Application Images PreservedFailuresAndDisappearance", ImageBrowsingChecks.PreservedFailuresAndDisappearance),
    ("Application Images SelectionRacesAndClearing", ImageBrowsingChecks.SelectionRacesAndClearing),
    ("Application Images BoundedRefreshAndDeadline", ImageBrowsingChecks.BoundedRefreshAndDeadline),
    ("Application Images ReconnectInvalidationAndLateReads", ImageBrowsingChecks.ReconnectInvalidationAndLateReads),
    ("Application Images ResourceDetectedLossAndRecovery", ImageBrowsingChecks.ResourceDetectedLossAndRecovery),
    ("Application Images LivenessLossAndShutdown", ImageBrowsingChecks.LivenessLossAndShutdown),
    ("Images negotiated API 1.40–1.56 schema fixtures", ImageChecks.ContractFixtures),
    ("Images authoritative immutable projections and exact Inspect", ImageChecks.Projection),
    ("Images reference escaping, invalid inputs and classified daemon failures", ImageChecks.ErrorsAndIdentity),
    ("Images malformed responses and bounded success/error bodies", ImageChecks.MalformedAndBounds),
    ("Images caller/header/body cancellation, deadlines and disposal", ImageChecks.CancellationAndDisposal),
    ("Images retained transport continuity races", ImageChecks.Continuity),
    ("Images late header/body results and decoder publication fences", ImageChecks.LateResponses),
    ("Presentation Logs bounded windows, search and sustained load", LogPresentationChecks.WindowsAndLoad),
    ("Presentation Logs mutable entries, revision races, Clear and TTY", LogPresentationChecks.RacesAndClear),
    ("Presentation Logs asynchronous close ownership and navigation", LogPresentationChecks.OwnershipAndNavigation),
    ("Presentation Logs browser tabs, selection and lifetime", LogPresentationChecks.BrowserLifetime),
    ("Application Logs fragmented assembly, bounded retention and notifications", LogSessionChecks.AssemblyBoundsAndNotifications),
    ("Application Logs Clear, close, replacement and joined late openings", LogSessionChecks.ClearCloseAndLateReplacement),
    ("Application Logs terminal failures, deletion, selection and finite EOF", LogSessionChecks.TerminalDeletionSelectionAndEof),
    ("Application Logs precise recovery, replay multiplicity and authoritative evidence", LogSessionChecks.RecoveryPrecisionMultiplicityAndAuthority),
    ("Application Logs joins asynchronous disposal and bounds unfinished eviction", LogSessionChecks.DisposalJoinAndUnfinishedEviction),
    ("Application Logs incomplete replay evidence and cancellation during recovery", LogSessionChecks.RecoveryEvidenceAndCancellation),
    ("Application Logs bounded recovery, Engine identity and operation coexistence", LogSessionChecks.RecoveryLimitsIdentityAndCoexistence),
    ("Logs negotiated API 1.40–1.56 contracts and fragmented frames/UTF-8", LogChecks.ContractsAndFragmentation),
    ("Logs authoritative TTY, raw preservation and exact timestamps", LogChecks.TtyAndPrecision),
    ("Logs bounded long output, malformed framing/text and partial EOF", LogChecks.LongOutputAndMalformed),
    ("Logs parameter validation and bounded classified daemon errors", LogChecks.ErrorsAndValidation),
    ("Logs bounded pressure, cancellation, disposal and admission", LogChecks.PressureCancellationAndAdmission),
    ("Logs opening deadlines, long follow and transport continuity", LogChecks.LifetimesAndContinuity),
    ("Logs opening/disposal races and concurrent admission", LogChecks.OpeningDisposalRaces),
    ("Exec named-pipe coexistence with logs events stats resources commands and liveness", LogChecks.SimultaneousNamedPipe),
    ("Lifecycle coordinator observation diagnostics and canceled restart preflight", OperationCoordinatorChecks.ObservationFailuresAndCanceledPreflight),
    ("Lifecycle coordinator explicit disconnect and unrelated detail progress", OperationCoordinatorChecks.DisconnectAndUnrelatedInspectionProgress),
    ("Lifecycle coordinator observation transport loss and joined late list", OperationCoordinatorChecks.ObservationLossAndJoinedLateList),
    ("Lifecycle coordinator acknowledged transitions and restart evidence", OperationCoordinatorChecks.AcknowledgedTransitionsAndRestartEvidence),
    ("Lifecycle coordinator classified rejections and safe Delete choices", OperationCoordinatorChecks.RejectionsDeleteAndExplicitForce),
    ("Lifecycle coordinator cancellation evidence and bounded uncertainty", OperationCoordinatorChecks.CancellationAndUnknownOutcomes),
    ("Lifecycle coordinator duplicate bounds and scoped full identities", OperationCoordinatorChecks.DuplicateBoundsAndGenerationValidation),
    ("Lifecycle coordinator stale reads, event races and deselected targets", OperationCoordinatorChecks.StaleReadsEventRacesAndDeselectedTargets),
    ("Lifecycle coordinator selected Inspect, disappearance and navigation", OperationCoordinatorChecks.SelectedInspectionDisappearanceAndNavigation),
    ("Lifecycle coordinator applied mutation, lost response and no replay", OperationCoordinatorChecks.LostResponseRecoveryWithoutReplay),
    ("Lifecycle coordinator joined retry/disconnect and late shutdown", OperationCoordinatorChecks.JoinedRetryDisconnectAndLateShutdown),
    ("Reconciliation initial snapshot boundaries and authoritative properties", ReconciliationChecks.SnapshotBoundariesAndAuthority),
    ("Reconciliation sustained duplicate bursts and detail fairness", ReconciliationChecks.BurstProgressAndDetails),
    ("Reconciliation external deletion and close-detail races", ReconciliationChecks.DeletionAndCloseRaces),
    ("Reconciliation precise reconnect overlap and full resync", ReconciliationChecks.ReconnectOverlapAndFullResync),
    ("Reconciliation classified stream diagnostics and bounded recovery", ReconciliationChecks.FailureDiagnosticsAndBoundedRecovery),
    ("Reconciliation Engine identity and opening transport loss", ReconciliationChecks.EngineIdentityAndOpeningTransportFailure),
    ("Reconciliation late opening/read completion and joined shutdown", ReconciliationChecks.LateOpeningReadsAndShutdown),
    ("Reconciliation event transport loss joins outstanding reads", ReconciliationChecks.EventLossJoinsOutstandingReads),
    ("Presentation automatic state updates and stale Inspect invalidation", ReconciliationChecks.PresentationAutomaticInvalidation),
    ("Stream termination logs coordinated reader and cleanup", () => StreamTerminationChecks.ReaderCrossesCleanup(logs: true)),
    ("Stream termination events coordinated reader and cleanup", () => StreamTerminationChecks.ReaderCrossesCleanup(logs: false)),
    ("Stream termination logs cancellation and replacement fences", () => StreamTerminationChecks.CancellationAndReplacement(logs: true)),
    ("Stream termination events cancellation and replacement fences", () => StreamTerminationChecks.CancellationAndReplacement(logs: false)),
    ("Event API 1.40–1.56 schemas, fragmented/coalesced reads", EventChecks.SchemasAndFragmentation),
    ("Event precision, duplicates, optional fields and replay cursor", EventChecks.TimestampsDuplicatesAndCursor),
    ("Event bounded slow-consumer backpressure and subscription limit", EventChecks.BoundedSlowConsumer),
    ("Event malformed/oversized input and exact size boundary", EventChecks.InvalidAndOversized),
    ("Event HTTP diagnostics, denied access and transport loss", EventChecks.HttpAndTransportFailures),
    ("Event opening deadline and established long stream lifetime", EventChecks.OpeningAndLongLifetime),
    ("Event cancellation, connection disposal and races", EventChecks.CancellationAndDisposal),
    ("Event independently negotiated metadata and continuity rejection", EventChecks.ContinuityAndMetadata),
    ("Event format negotiation, late opening rejection and blocked cancellation", EventChecks.FormatsAndLateOpening),
    ("Named-pipe active event stream with container reads/liveness", EventChecks.ConcurrentNamedPipe),
    ("Inspect structure, exact copying, search and replacement", InspectChecks.StructureAndSearch),
    ("Inspect selected session refresh and lifetime", ContainerPresentationChecks.InspectLifetime),
    ("Presentation lifecycle availability and single admission", ContainerPresentationChecks.LifecycleAvailability),
    ("Presentation deletion capture, cancellation and parameters", ContainerPresentationChecks.DeleteSafety),
    ("Presentation coordinator admission and unresolved diagnostics", ContainerPresentationChecks.OperationAdmissionDiagnostics),
    ("Presentation retained outcomes, notifications and navigation", ContainerPresentationChecks.OperationLifetime),
    ("Volumes presentation MetadataSearchAndExactIdentity", VolumePresentationChecks.MetadataSearchAndExactIdentity),
    ("Volumes presentation EmptyEvidenceMatrix", VolumePresentationChecks.EmptyEvidenceMatrix),
    ("Volumes presentation RetainedRowsAndDetailBrowsing", VolumePresentationChecks.RetainedRowsAndDetailBrowsing),
    ("Volumes presentation WarningsReplacementAndDeliberateReveal", VolumePresentationChecks.WarningsReplacementAndDeliberateReveal),
    ("Volumes presentation OverviewInspectAndExternalInvalidation", VolumePresentationChecks.OverviewInspectAndExternalInvalidation),
    ("Volumes presentation QueuedParsingFilteringOverviewAndCopyFences", VolumePresentationChecks.QueuedParsingFilteringOverviewAndCopyFences),
    ("Volumes presentation CompleteDisappearanceAndNotFound", VolumePresentationChecks.CompleteDisappearanceAndNotFound),
    ("Volumes presentation NotificationsNavigationAndRepeatedAttachment", VolumePresentationChecks.NotificationsNavigationAndRepeatedAttachment),
    ("Volumes presentation LargeCollectionsAndSupersededSearch", VolumePresentationChecks.LargeCollectionsAndSupersededSearch),
    ("Network deletion presentation BuiltInsAndClassifiedBlockedEvidence", NetworkOperationPresentationChecks.BuiltInsAndClassifiedBlockedEvidence),
    ("Network deletion presentation LoadingCancelFailureAndExactCapture", NetworkOperationPresentationChecks.LoadingCancelFailureAndExactCapture),
    ("Network deletion presentation AttachmentsUnavailableAndBoundedFullCopy", NetworkOperationPresentationChecks.AttachmentsUnavailableAndBoundedFullCopy),
    ("Network deletion presentation ExactSingleUseAndSessionOwnership", NetworkOperationPresentationChecks.ExactSingleUseAndSessionOwnership),
    ("Network deletion presentation RevisionsReplacementAndLiveInvalidation", NetworkOperationPresentationChecks.RevisionsReplacementAndLiveInvalidation),
    ("Network deletion presentation BlockedRevisionNeverRenewsConfirmation", NetworkOperationPresentationChecks.BlockedRevisionNeverRenewsConfirmation),
    ("Network deletion presentation SelectionNavigationDisconnectAndDelayedCallbacks", NetworkOperationPresentationChecks.SelectionNavigationDisconnectAndDelayedCallbacks),
    ("Network deletion presentation ConflictRetainedEvidenceAndFreshExplicitAttempt", NetworkOperationPresentationChecks.ConflictRetainedEvidenceAndFreshExplicitAttempt),
    ("Network deletion presentation OffThreadCoalescingPrivateRevealAndCopyFences", NetworkOperationPresentationChecks.OffThreadCoalescingPrivateRevealAndCopyFences),
    ("Network deletion presentation CommandOutcomeDistinctions", NetworkOperationPresentationChecks.CommandOutcomeDistinctions),
    ("Network deletion presentation ExternalPendingAndRejectedAdmission", NetworkOperationPresentationChecks.ExternalPendingAndRejectedAdmission),
    ("Networks presentation MetadataAndSearch", NetworkPresentationChecks.MetadataAndSearch),
    ("Networks presentation StatesAndCoalescing", NetworkPresentationChecks.StatesAndCoalescing),
    ("Networks presentation IdentityOverviewAndAdmission", NetworkPresentationChecks.IdentityOverviewAndAdmission),
    ("Networks presentation InspectContinuityAndExternalInvalidation", NetworkPresentationChecks.InspectContinuityAndExternalInvalidation),
    ("Networks presentation StaleQueuedResultsAndCopy", NetworkPresentationChecks.StaleQueuedResultsAndCopy),
    ("Networks presentation NavigationAndReattachment", NetworkPresentationChecks.NavigationAndReattachment),
    ("Networks presentation LargeCollectionRapidSearch", NetworkPresentationChecks.LargeCollectionRapidSearch),
    ("Images presentation metadata and loaded search", ImagePresentationChecks.MetadataAndSearch),
    ("Images presentation distinct states and notification coalescing", ImagePresentationChecks.StatesAndCoalescing),
    ("Images presentation full identity, Overview and intentional admission", ImagePresentationChecks.IdentityOverviewAndAdmission),
    ("Images presentation exact Inspect and external invalidation", ImagePresentationChecks.InspectContinuityAndExternalInvalidation),
    ("Images presentation stale queued results and copying", ImagePresentationChecks.StaleQueuedResultsAndCopy),
    ("Images presentation navigation cancellation and reattachment", ImagePresentationChecks.NavigationAndReattachment),
    ("Images presentation large collection and superseded searches", ImagePresentationChecks.LargeCollectionRapidSearch),
    ("Images deletion confirmation cancellation and single-use exact ID", ImageOperationPresentationChecks.CancellationAndSingleUse),
    ("Images deletion queued selection and generation races", ImageOperationPresentationChecks.QueuedStateAndGenerationRaces),
    ("Images deletion obsolete confirmation and pending admission", ImageOperationPresentationChecks.ObsoleteConfirmationAndPendingAdmission),
    ("Images deletion disappearance and independent evidence", ImageOperationPresentationChecks.DisappearanceAndIndependentEvidence),
    ("Images deletion classified outcome display", ImageOperationPresentationChecks.ClassifiedOutcomeDisplay),
    ("Images deletion off-thread notifications and sensitive reveal", ImageOperationPresentationChecks.OffThreadCoalescingAndSensitiveReveal),
    ("Images deletion navigation retention and late completion", ImageOperationPresentationChecks.NavigationRetentionAndLateCompletion),
    ("Images deletion teardown and reattachment dialog fences", ImageOperationPresentationChecks.TeardownAndReattachmentDialogFences),
    ("Presentation search fields and unknown metadata", ContainerPresentationChecks.SearchAndUnknownMetadata),
    ("Presentation distinct states and latest notifications", ContainerPresentationChecks.DistinctStatesAndLatestNotifications),
    ("Presentation identity selection, Overview and cancellation", ContainerPresentationChecks.SelectionDetailsAndCancellation),
    ("Presentation container diagnostic refresh and stale-detail clearing", ContainerPresentationChecks.DiagnosticsLifetime),
    ("Presentation disconnect, generation and navigation invalidation", ContainerPresentationChecks.InvalidationAndNavigation),
    ("Presentation large collection and rapid search", ContainerPresentationChecks.LargeCollectionAndRapidSearch),
    ("Browsing initial load, empty list, refresh replacement and raw inspect", ContainerBrowsingChecks.InitialEmptyAndReplacement),
    ("Browsing classified failures and external disappearance", ContainerBrowsingChecks.PreservedFailuresAndDisappearance),
    ("Browsing superseded selections and inspection clearing", ContainerBrowsingChecks.SelectionRacesAndClearing),
    ("Browsing bounded refresh work and request deadline", ContainerBrowsingChecks.BoundedRefreshAndDeadline),
    ("Browsing reconnect invalidation rejects late reads", ContainerBrowsingChecks.ReconnectInvalidationAndLateReads),
    ("Browsing resource-detected loss requires fresh negotiation", ContainerBrowsingChecks.ResourceDetectedLossAndRecovery),
    ("Browsing liveness loss and shutdown with reads in flight", ContainerBrowsingChecks.LivenessLossAndShutdown),
    ("Command official endpoint/status contracts API 1.40–1.56", CommandChecks.OfficialContracts),
    ("Volume deletion official contracts API 1.40–1.56", VolumeCommandChecks.OfficialContracts),
    ("Volume deletion exact original names and classified outcomes", VolumeCommandChecks.NamesAndFailures),
    ("Volume deletion original, fragmented and bounded errors with canceled response evidence", VolumeCommandChecks.ErrorsAndBounds),
    ("Volume deletion uncertainty, lifetimes and shared command admission", VolumeCommandChecks.LifetimesAndSharedAdmission),
    ("Volume deletion late successful responses and lifetime fences", VolumeCommandChecks.LateResponseFences),
    ("Named-pipe volume deletion loss without replacement or replay", VolumeCommandChecks.NamedPipeNoReplay),
    ("Named-pipe volume deletion cancellation, deadline and disposal", VolumeCommandChecks.NamedPipeIssuedLifetimes),
    ("Image deletion official contracts API 1.40–1.56", ImageCommandChecks.OfficialContracts),
    ("Image deletion safe references, untagging and classified failures", ImageCommandChecks.ReferencesAndFailures),
    ("Image deletion fragmented, malformed and bounded responses", ImageCommandChecks.ResponsesAndBounds),
    ("Image deletion uncertainty, decoding cancellation and shared command admission", ImageCommandChecks.LifetimesAndSharedAdmission),
    ("Image deletion late successful response cancellation, deadline and continuity fences", ImageCommandChecks.LateResponseFences),
    ("Named-pipe image deletion loss and no implicit replacement/replay", ImageCommandChecks.NamedPipeNoReplay),
    ("Named-pipe image deletion issued cancellation, deadline and disposal", ImageCommandChecks.NamedPipeIssuedLifetimes),
    ("Command safe parameters and endpoint-specific failures", CommandChecks.ParametersAndFailures),
    ("Command invalid identity, cancellation and stale connection before dispatch", CommandChecks.BeforeDispatch),
    ("Command uncertain outcomes, cancellation, deadlines, disposal and continuity", CommandChecks.UncertainLifetimes),
    ("Command bounded bodies, capacity and graceful deadlines", CommandChecks.BoundsAndGracefulLifetime),
    ("Named-pipe applied command, lost response and no replay to replacement", CommandChecks.NamedPipeNoReplay),
    ("Named-pipe issued command cancellation, deadline and disposal cleanup", CommandChecks.NamedPipeIssuedCancellation),
    ("Container contract fixtures across API 1.40–1.56", ContainerChecks.ContractFixtures),
    ("Container states, optional/additive fields and inspect preservation", ContainerChecks.Projection),
    ("Container diagnostic schema, unknown values, malformed types and raw preservation", ContainerChecks.Diagnostics),
    ("Container encoded identities and classified daemon errors", ContainerChecks.ErrorsAndIdentity),
    ("Container malformed and bounded responses", ContainerChecks.MalformedAndBounds),
    ("Container cancellation, deadlines and disposal", ContainerChecks.CancellationAndDisposal),
    ("Container retained transport continuity", ContainerChecks.Continuity),
    ("Coordinator initial failure and recovery", CoordinatorChecks.InitialRecovery),
    ("Coordinator healthy retention and fresh negotiation after loss", CoordinatorChecks.HealthyAndLost),
    ("Coordinator preserved failure distinctions and bounded attempts", CoordinatorChecks.FailuresAndBounds),
    ("Coordinator retries reject late successes without overlap", CoordinatorChecks.RetryRace),
    ("Coordinator cancellation during liveness and shutdown cleanup", CoordinatorChecks.Shutdown),
    ("Coordinator deadlines and shutdown during recovery", CoordinatorChecks.DeadlinesAndRecoveryShutdown),
    ("Coordinator shutdown rejects a late successful handshake", CoordinatorChecks.ShutdownLateSuccess),
    ("Transport reopening invalidates metadata", TransportReopening),
    ("Retained transport liveness, failure, cancellation and deadline", Liveness),
    ("API 1.40 schema metadata and actual request paths", SchemaAndPaths),
    ("Older and newer overlapping daemon ranges", CompatibleRanges),
    ("Current API 1.56 schema subset", CurrentSchema),
    ("Incompatible ranges never request info", IncompatibleRanges),
    ("Optional and additive fields", OptionalAndAdditive),
    ("HTTP errors preserve status and daemon message", HttpErrors),
    ("Malformed, absent, wrong-type and oversized responses", MalformedResponses),
    ("Cancellation and disposal at each handshake phase", Cancellation),
    ("Cancellation of a stalled response body", BodyCancellation),
    ("Request and response body deadlines", Deadlines),
    ("Transport failure classifications and disposal", TransportFailures),
    ("Explicit endpoint and platform rejection", EndpointRejection),
    ("Named-pipe HTTP handshake and successful-session disposal", ControlledPipe),
    ("Named-pipe failed and canceled attempt disposal", FailedAndCanceledPipe),
    ("Named-pipe missing endpoint and canceled connect", MissingAndCanceledPipe)
};
if (args.FirstOrDefault() == "--exec")
    tests = tests.Where(test => test.Item1.StartsWith("Exec ", StringComparison.Ordinal)).ToArray();
if (args.FirstOrDefault() == "--stats-presentation")
    tests = tests.Where(test => test.Item1.StartsWith("Stats presentation ", StringComparison.Ordinal)).ToArray();
if (args.FirstOrDefault() == "--stats")
    tests = tests.Where(test => test.Item1.StartsWith("Stats ", StringComparison.Ordinal)).ToArray();
if (args.FirstOrDefault() == "--stream-termination")
    tests = tests.Where(test => test.Item1.StartsWith("Stream termination ", StringComparison.Ordinal)).ToArray();
if (args.FirstOrDefault() == "--network-deletion-presentation")
    tests = tests.Where(test => test.Item1.StartsWith("Network deletion presentation", StringComparison.Ordinal)).ToArray();
if (args.FirstOrDefault() == "--network-preparation")
    tests = tests.Where(test => test.Item1.StartsWith("Network preparation ", StringComparison.Ordinal)).ToArray();
if (args.FirstOrDefault() == "--network-operations")
    tests = tests.Where(test => test.Item1.StartsWith("Network operations ", StringComparison.Ordinal)).ToArray();
int failed = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} groups passed on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}.");
return failed == 0 ? 0 : 1;

static string Version(string minimum = "1.12", string maximum = "1.40")
    => $$"""{"Version":"19.03.12","ApiVersion":"{{maximum}}","MinAPIVersion":"{{minimum}}","Os":"linux","Arch":"amd64","KernelVersion":"4.19.76-linuxkit"}""";
static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    => new(status) { Content = new TrackedContent(body) };
static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
static async Task ExpectCancellation(Func<Task> action)
{
    try { await action(); throw new InvalidOperationException("Cancellation was not propagated"); }
    catch (OperationCanceledException) { }
}
static FakeTransport Ready(string? version = null, string? info = null)
    => new((_, index, _) => Task.FromResult(Response(index switch { 0 => "OK", 1 => version ?? Version(), _ => info ?? Info })));
static async Task SchemaAndPaths()
{
    var transport = Ready();
    var attempt = await new DockerEngineConnectionClient(transport).ConnectAsync();
    Check(attempt.IsConnected);
    Check(attempt.Failure is null);
    var c = attempt.Connection!;
    var m = c.Metadata;
    Check(m.Endpoint == EngineEndpoint.LocalWindows);
    Check(m.EngineVersion == "19.03.12" && m.RuntimeOS == "linux" && m.RuntimeArchitecture == "amd64");
    Check(m.HostArchitecture == "x86_64" && m.KernelVersion == "4.19.76-linuxkit" && m.OperatingSystem == "Alpine Linux v3.5");
    Check(m.EngineId == "engine-id" && m.EngineName == "engine-name" && m.InfoServerVersion == "19.03.12");
    Check(m.Capabilities.MemoryLimit == true && m.Capabilities.SwapLimit == false && m.Capabilities.SwarmLocalNodeState == "inactive");
    Check(m.Api.Negotiated == new EngineApiVersion(1, 40));
    Check(transport.Handler.Paths.SequenceEqual(new[] { "/_ping", "/version", "/v1.40/info" }));
    Check(!transport.Handler.Disposed, "Successful session must retain reusable transport");
    Check(transport.Handler.Contents.All(c => c.Disposed), "Every response must be released");
    await c.DisposeAsync(); await c.DisposeAsync();
    Check(transport.Handler.DisposeCount == 1, "Session disposal must be idempotent");
}
static async Task TransportReopening()
{
    var transport = new ContinuityTestTransport();
    var attempt = await new DockerEngineConnectionClient(transport).ConnectAsync();
    Check(attempt.IsConnected);
    await using var connection = attempt.Connection!;
    Check(await connection.CheckLivenessAsync() is null);
    transport.Counter.Opened();
    Check((await connection.CheckLivenessAsync())?.Kind == EngineConnectionFailureKind.TransientTransportFailure);
    // Simulate the HTTP pool opening a fresh pipe during ping rather than before it.
    var reopening = new ContinuityTestTransport(reopenAt: 3);
    var second = await new DockerEngineConnectionClient(reopening).ConnectAsync();
    await using var secondConnection = second.Connection!;
    Check((await secondConnection.CheckLivenessAsync())?.Kind == EngineConnectionFailureKind.TransientTransportFailure);
    var interrupted = new ContinuityTestTransport(reopenAt: 2);
    var failed = await new DockerEngineConnectionClient(interrupted).ConnectAsync();
    Check(failed.Failure?.Kind == EngineConnectionFailureKind.TransientTransportFailure && interrupted.Handler.Disposed);
}
static async Task Liveness()
{
    var transport = new FakeTransport((_, index, token) =>
    {
        if (index == 5) return Task.FromException<HttpResponseMessage>(new IOException("pipe lost"));
        return Task.FromResult(Response(index switch { 0 or 3 => "OK", 1 => Version(), 2 => Info,
            4 => "not OK", _ => "OK" }));
    });
    var result = await new DockerEngineConnectionClient(transport).ConnectAsync();
    await using var c = result.Connection!;
    Check(await c.CheckLivenessAsync() is null);
    Check((await c.CheckLivenessAsync())?.Kind == EngineConnectionFailureKind.MalformedResponse);
    Check((await c.CheckLivenessAsync())?.Kind == EngineConnectionFailureKind.TransientTransportFailure);
    Check(transport.Handler.Paths.Skip(3).All(p => p == "/_ping"), "Health checks must not renegotiate");
    Check(!transport.Handler.Disposed && transport.Handler.Contents.All(c => c.Disposed));
    using var cancel = new CancellationTokenSource();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var blocked = new FakeTransport(async (_, index, token) =>
    {
        if (index >= 3) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        return Response(index switch { 0 => "OK", 1 => Version(), _ => Info });
    });
    var ready = await new DockerEngineConnectionClient(blocked, new() { RequestTimeout = TimeSpan.FromMilliseconds(80) }).ConnectAsync();
    var pending = ready.Connection!.CheckLivenessAsync(cancel.Token);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
    await ExpectCancellation(async () => await pending);
    Check((await ready.Connection.CheckLivenessAsync())?.Kind == EngineConnectionFailureKind.TransientTransportFailure);
    await ready.Connection.DisposeAsync();
    Check(blocked.Handler.Disposed);
    var errorTransport = new FakeTransport((_, index, _) => Task.FromResult(index < 3
        ? Response(index switch { 0 => "OK", 1 => Version(), _ => Info })
        : Response("""{"message":"deliberate private daemon message"}""", HttpStatusCode.ServiceUnavailable)));
    var errorReady = await new DockerEngineConnectionClient(errorTransport).ConnectAsync();
    await using var errorConnection = errorReady.Connection!;
    var failure = await errorConnection.CheckLivenessAsync();
    Check(failure?.Kind == EngineConnectionFailureKind.DaemonHttpError && failure.DaemonMessage == "deliberate private daemon message");
}
static async Task CompatibleRanges()
{
    foreach (var (minimum, maximum, expected) in new[] { ("1.12", "1.40", "1.40"), ("1.24", "1.41", "1.41"),
        ("1.44", "1.52", "1.52"), ("1.44", "1.53", "1.53"), ("1.40", "1.56", "1.56"), ("1.44", "1.99", "1.56") })
    {
        var transport = Ready(Version(minimum, maximum));
        var result = await new DockerEngineConnectionClient(transport).ConnectAsync();
        Check(result.IsConnected);
        await using var connection = result.Connection!;
        Check(connection.Metadata.Api.DaemonMinimum.ToString() == minimum && connection.Metadata.Api.DaemonMaximum.ToString() == maximum);
        Check(connection.Metadata.Api.Negotiated.ToString() == expected);
        Check(transport.Handler.Paths.Last() == $"/v{expected}/info");
    }
    for (int minor = 40; minor <= 56; minor++)
    {
        var transport = Ready(Version("1.40", $"1.{minor}"));
        var result = await new DockerEngineConnectionClient(transport).ConnectAsync();
        Check(result.IsConnected);
        await using var connection = result.Connection!;
        Check(connection.Metadata.Api.Negotiated == new EngineApiVersion(1, minor));
        Check(transport.Handler.Paths.Last() == $"/v1.{minor}/info");
    }
    Check(new EngineApiVersion(1, 9).CompareTo(new(1, 40)) < 0, "API versions must compare numerically");
}
static async Task CurrentSchema()
{
    // Synthetic selected-field fixture reviewed against official 1.56 SystemVersion/SystemInfo;
    // not a captured real daemon response. New nested fields are intentionally ignored.
    const string version = """{"Version":"29.8.2","ApiVersion":"1.56","MinAPIVersion":"1.40","Os":"linux","Arch":"amd64","KernelVersion":"6.8.0-31-generic","Platform":{"Name":"Docker Engine - Community"},"Components":[{"Name":"Engine","Version":"29.8.2","Details":{}}]}""";
    const string info = """{"ID":"test-current-engine","Name":"fixture","OperatingSystem":"Ubuntu 24.04 LTS","OSType":"linux","Architecture":"x86_64","ServerVersion":"29.8.2","MemoryLimit":true,"PidsLimit":true,"Swarm":{"LocalNodeState":"inactive"},"NRI":{"Enabled":false},"DiscoveredDevices":[],"Containerd":{"Address":"fixture-socket"}}""";
    var transport = Ready(version, info);
    var result = await new DockerEngineConnectionClient(transport).ConnectAsync();
    Check(result.IsConnected);
    await using var connection = result.Connection!;
    Check(connection.Metadata.EngineVersion == "29.8.2" && connection.Metadata.Api.Negotiated == new EngineApiVersion(1, 56));
    Check(connection.Metadata.Capabilities.Experimental is null && connection.Metadata.Capabilities.PidsLimit == true);
    Check(transport.Handler.Paths.Last() == "/v1.56/info");
}
static async Task IncompatibleRanges()
{
    foreach (var (minimum, maximum) in new[] { ("1.12", "1.39"), ("1.57", "1.99"), ("2.0", "2.1") })
    {
        var transport = Ready(Version(minimum, maximum));
        var result = await new DockerEngineConnectionClient(transport).ConnectAsync();
        Check(result.Failure?.Kind == EngineConnectionFailureKind.IncompatibleApi);
        Check(transport.Handler.Paths.Count == 2 && transport.Handler.Disposed);
    }
}
static async Task OptionalAndAdditive()
{
    var version = """{"Version":"test-engine","ApiVersion":"1.80","MinAPIVersion":"1.20","Extra":{"unknown":[1,2]}}""";
    var info = """{"Extra":{"a":true},"Name":null,"MemoryLimit":null,"Swarm":null}""";
    var transport = Ready(version, info);
    var result = await new DockerEngineConnectionClient(transport).ConnectAsync();
    Check(result.IsConnected);
    await using var connection = result.Connection!;
    var m = connection.Metadata;
    Check(m.RuntimeOS is null && m.RuntimeArchitecture is null && m.OSType is null && m.EngineId is null);
    Check(m.HostArchitecture is null && m.Capabilities.MemoryLimit is null && m.Capabilities.Experimental is null);
    var empty = Ready(version, "{}");
    var emptyResult = await new DockerEngineConnectionClient(empty).ConnectAsync();
    Check(emptyResult.IsConnected); await emptyResult.Connection!.DisposeAsync();
}
static async Task HttpErrors()
{
    foreach (int stage in new[] { 0, 1, 2 })
    {
        const string message = "daemon says: failed for private-example";
        var transport = new FakeTransport((_, index, _) => Task.FromResult(index == stage
            ? Response("""{"message":"daemon says: failed for private-example","extra":2}""", HttpStatusCode.Forbidden)
            : Response(index == 0 ? "OK" : Version())));
        var result = await new DockerEngineConnectionClient(transport).ConnectAsync();
        Check(result.Failure?.Kind == EngineConnectionFailureKind.DaemonHttpError && result.Failure.HttpStatusCode == 403);
        Check(result.Failure!.DaemonMessage == message);
        Check(!result.Failure.Description.Contains("private-example"), "Raw daemon content must stay separate from routine diagnostics");
        Check(transport.Handler.Disposed && transport.Handler.Contents.All(c => c.Disposed));
    }
    var plain = new FakeTransport((_, _, _) => Task.FromResult(Response("plain daemon error", HttpStatusCode.InternalServerError)));
    var error = await new DockerEngineConnectionClient(plain).ConnectAsync();
    Check(error.Failure?.DaemonMessage == "plain daemon error");
    var large = new FakeTransport((_, _, _) => Task.FromResult(Response(new string('x', 1025), HttpStatusCode.BadGateway)));
    var largeError = await new DockerEngineConnectionClient(large, new() { MaximumResponseBytes = 1024 }).ConnectAsync();
    Check(largeError.Failure?.Kind == EngineConnectionFailureKind.DaemonHttpError && largeError.Failure.HttpStatusCode == 502 && largeError.Failure.DaemonMessage is null);
}
static async Task MalformedResponses()
{
    foreach (var version in new[] { "{bad", "[]", "{}", Version("1.60", "1.40"), Version("bad", "1.40"),
        """{"Version":"x","ApiVersion":140,"MinAPIVersion":"1.12"}""", """{"Version":"x","ApiVersion":"1.40"}""" })
    {
        var transport = Ready(version);
        var result = await new DockerEngineConnectionClient(transport).ConnectAsync();
        Check(result.Failure?.Kind == EngineConnectionFailureKind.MalformedResponse);
        Check(transport.Handler.Paths.Count == 2 && transport.Handler.Disposed);
    }
    foreach (var info in new[] { "{bad", "null", """{"MemoryLimit":"yes"}""", """{"Architecture":2}""", """{"Swarm":[]}""" })
    {
        var transport = Ready(info: info);
        var result = await new DockerEngineConnectionClient(transport).ConnectAsync();
        Check(result.Failure?.Kind == EngineConnectionFailureKind.MalformedResponse && transport.Handler.Disposed);
    }
    var ping = new FakeTransport((_, _, _) => Task.FromResult(Response("not ready")));
    Check((await new DockerEngineConnectionClient(ping).ConnectAsync()).Failure?.Kind == EngineConnectionFailureKind.MalformedResponse);
    var large = Ready(info: new string('x', 1025));
    var oversized = await new DockerEngineConnectionClient(large, new() { MaximumResponseBytes = 1024 }).ConnectAsync();
    Check(oversized.Failure?.Kind == EngineConnectionFailureKind.MalformedResponse && large.Handler.Disposed);
    var unknownContent = new UnknownLengthContent(new string('x', 1025));
    var unknown = new FakeTransport((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = unknownContent }));
    var unknownResult = await new DockerEngineConnectionClient(unknown, new() { MaximumResponseBytes = 1024 }).ConnectAsync();
    Check(unknownResult.Failure?.Kind == EngineConnectionFailureKind.MalformedResponse && unknownContent.Disposed && unknown.Handler.Disposed);
}
static async Task Cancellation()
{
    foreach (int stage in new[] { 0, 1, 2 })
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(async (_, index, token) =>
        {
            if (index == stage) { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Response(index == 0 ? "OK" : Version());
        });
        var pending = new DockerEngineConnectionClient(transport).ConnectAsync(cancellationToken: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
        await ExpectCancellation(async () => await pending);
        Check(transport.Handler.Disposed && transport.Handler.Contents.All(c => c.Disposed));
    }
    using var already = new CancellationTokenSource(); already.Cancel();
    var unused = Ready();
    await ExpectCancellation(async () => await new DockerEngineConnectionClient(unused).ConnectAsync(cancellationToken: already.Token));
    Check(!unused.Created, "Pre-cancellation must not create a transport");
}
static async Task BodyCancellation()
{
    using var cancel = new CancellationTokenSource();
    var stream = new StalledStream();
    var transport = new FakeTransport((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
    var pending = new DockerEngineConnectionClient(transport).ConnectAsync(cancellationToken: cancel.Token);
    await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
    await ExpectCancellation(async () => await pending);
    Check(stream.Disposed && transport.Handler.Disposed);
}
static async Task Deadlines()
{
    var transport = new FakeTransport(async (_, _, token) => { await Task.Delay(Timeout.Infinite, token); return Response("OK"); });
    var result = await new DockerEngineConnectionClient(transport, new() { RequestTimeout = TimeSpan.FromMilliseconds(80) }).ConnectAsync();
    Check(result.Failure?.Kind == EngineConnectionFailureKind.TransientTransportFailure && transport.Handler.Disposed);
    var stream = new StalledStream();
    var body = new FakeTransport((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
    var bodyResult = await new DockerEngineConnectionClient(body, new() { RequestTimeout = TimeSpan.FromMilliseconds(80) }).ConnectAsync();
    Check(bodyResult.Failure?.Kind == EngineConnectionFailureKind.TransientTransportFailure && stream.Disposed && body.Handler.Disposed);
}
static async Task TransportFailures()
{
    foreach (var (exception, kind) in new (Exception, EngineConnectionFailureKind)[]
    {
        (new HttpRequestException("wrapped", new UnauthorizedAccessException()), EngineConnectionFailureKind.AccessDenied),
        (new HttpRequestException("wrapped", new FileNotFoundException()), EngineConnectionFailureKind.MissingEndpoint),
        (new IOException("broken"), EngineConnectionFailureKind.TransientTransportFailure),
        (new HttpRequestException("broken"), EngineConnectionFailureKind.TransientTransportFailure)
    })
    {
        var transport = new FakeTransport((_, _, _) => Task.FromException<HttpResponseMessage>(exception));
        var result = await new DockerEngineConnectionClient(transport).ConnectAsync();
        Check(result.Failure?.Kind == kind && transport.Handler.Disposed);
    }
}
static async Task EndpointRejection()
{
    foreach (string address in new[] { "unix:///var/run/docker.sock", "tcp://localhost:2375", "ssh://host", "npipe://remote/pipe/docker_engine", "npipe://./pipe/", "npipe://./pipe/../x" })
    {
        var unused = Ready();
        var result = await new DockerEngineConnectionClient(unused).ConnectAsync(new(address, EngineTransport.WindowsNamedPipe));
        Check(result.Failure?.Kind == EngineConnectionFailureKind.UnsupportedEndpoint && !unused.Created);
    }
    if (!OperatingSystem.IsWindows())
    {
        var result = await new DockerEngineConnectionClient().ConnectAsync();
        Check(result.Failure?.Kind == EngineConnectionFailureKind.UnsupportedEndpoint);
    }
}
static async Task ControlledPipe()
{
    // .NET supplies named pipes on Windows and Unix. permitTestHost affects only this internal fixture,
    // never production platform support. Unix evidence proves framing/ownership, not Win32 ACL semantics.
    var name = "cdd-" + Guid.NewGuid().ToString("N")[..8];
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var paths = new List<string>();
    var serving = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(deadline.Token);
        for (int stage = 0; stage < 6; stage++)
        {
            var headers = new StringBuilder();
            var one = new byte[1];
            while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (headers.Length > 8192 || await server.ReadAsync(one, deadline.Token) == 0) throw new IOException("Incomplete test HTTP request");
                headers.Append((char)one[0]);
            }
            paths.Add(headers.ToString().Split(' ')[1]);
            var bytes = Encoding.UTF8.GetBytes(stage switch { 0 or 3 => "OK", 1 => Version("1.12", "1.80"),
                4 => "[{\"Id\":\"retained\",\"State\":\"exited\"}]", 5 => "{\"Id\":\"retained\",\"Unknown\":\"kept\"}", _ => Info });
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nContent-Type: {(stage == 0 ? "text/plain" : "application/json")}\r\n\r\n");
            await server.WriteAsync(head, deadline.Token); await server.WriteAsync(bytes, deadline.Token); await server.FlushAsync(deadline.Token);
        }
        try { Check(await server.ReadAsync(new byte[1], deadline.Token) == 0, "Client pipe must close when session is disposed"); }
        catch (IOException) { /* Windows can report pipe break instead of EOF. */ }
    });
    var client = new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true));
    var result = await client.ConnectAsync(new("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe), deadline.Token);
    Check(result.IsConnected, result.Failure?.Description ?? "Pipe connection failed");
    Check(result.Connection!.Metadata.Api.Negotiated == new EngineApiVersion(1, 56));
    Check(await result.Connection.CheckLivenessAsync(deadline.Token) is null);
    var reader = (IContainerReader)result.Connection;
    Check((await reader.ListContainersAsync(deadline.Token)).Value![0].State == "exited");
    Check((await reader.InspectContainerAsync("retained", deadline.Token)).Value!.RawJson.Contains("kept"));
    await result.Connection.DisposeAsync();
    await serving;
    Check(paths.SequenceEqual(new[] { "/_ping", "/version", "/v1.56/info", "/_ping", "/v1.56/containers/json?all=true", "/v1.56/containers/retained/json" }));
}
static async Task FailedAndCanceledPipe()
{
    foreach (bool cancelAttempt in new[] { false, true })
    {
        var name = "cdd-" + Guid.NewGuid().ToString("N")[..8];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(deadline.Token);
            var headers = new StringBuilder(); var one = new byte[1];
            while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (headers.Length > 8192 || await server.ReadAsync(one, deadline.Token) == 0) throw new IOException("Incomplete request");
                headers.Append((char)one[0]);
            }
            received.SetResult();
            if (!cancelAttempt)
            {
                var body = Encoding.UTF8.GetBytes("""{"message":"fixture daemon unavailable"}""");
                await server.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 503 Service Unavailable\r\nContent-Length: {body.Length}\r\n\r\n"), deadline.Token);
                await server.WriteAsync(body, deadline.Token); await server.FlushAsync(deadline.Token);
            }
            try { Check(await server.ReadAsync(one, deadline.Token) == 0, "Failed/canceled client must close its pipe"); }
            catch (IOException) { }
        });
        var pending = new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true))
            .ConnectAsync(new("npipe://./pipe/" + name, EngineTransport.WindowsNamedPipe), cancel.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancelAttempt) { cancel.Cancel(); await ExpectCancellation(async () => await pending); }
        else
        {
            var result = await pending;
            Check(result.Failure?.Kind == EngineConnectionFailureKind.DaemonHttpError && result.Failure.HttpStatusCode == 503);
        }
        await serving;
    }
}
static async Task MissingAndCanceledPipe()
{
    var endpoint = new EngineEndpoint("npipe://./pipe/cdd-" + Guid.NewGuid().ToString("N")[..8], EngineTransport.WindowsNamedPipe);
    var client = new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true), new() { PipeConnectTimeout = TimeSpan.FromMilliseconds(100) });
    var result = await client.ConnectAsync(endpoint);
    Check(result.Failure?.Kind == EngineConnectionFailureKind.MissingEndpoint, result.Failure?.Description ?? "Unexpected success");
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    await ExpectCancellation(async () => await new DockerEngineConnectionClient(new NamedPipeTransport(permitTestHost: true),
        new() { PipeConnectTimeout = TimeSpan.FromSeconds(3) }).ConnectAsync(endpoint, cancel.Token));
}

sealed class FakeTransport(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : IDockerTransportFactory
{
    public TrackingHandler Handler { get; } = new(send);
    public bool Created { get; private set; }
    public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options) { Created = true; return Handler; }
}
sealed class TrackingHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public List<string> Paths { get; } = [];
    public List<TrackedContent> Contents { get; } = [];
    public int DisposeCount { get; private set; }
    public bool Disposed => DisposeCount > 0;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var index = Paths.Count; Paths.Add(request.RequestUri!.AbsolutePath);
        var response = await send(request, index, cancellationToken);
        if (response.Content is TrackedContent content) Contents.Add(content);
        return response;
    }
    protected override void Dispose(bool disposing) { if (disposing) DisposeCount++; base.Dispose(disposing); }
}
sealed class TrackedContent(string body) : StringContent(body)
{
    public bool Disposed { get; private set; }
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}
sealed class StalledStream : Stream
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use async read");
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}

sealed class UnknownLengthContent(string body) : HttpContent
{
    private readonly byte[] bytes = Encoding.UTF8.GetBytes(body);
    public bool Disposed { get; private set; }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}

sealed class ContinuityTestTransport(int reopenAt = -1) : IDockerTransportFactory
{
    public PipeConnectionCounter Counter { get; } = new();
    public TrackingHandler Handler { get; private set; } = null!;
    public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
    {
        Counter.Opened();
        Handler = new TrackingHandler((_, index, _) =>
        {
            if (index == reopenAt) Counter.Opened();
            string body = index switch
            {
                1 => """{"Version":"fixture","ApiVersion":"1.56","MinAPIVersion":"1.40"}""",
                2 => "{}", _ => "OK"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new TrackedContent(body) });
        });
        return new ContinuityHandler(Handler, Counter);
    }
}
