using System.Collections.Generic;
using System.Globalization;

namespace RemoteAgent.Localization;

internal static partial class Strings
{
    public const string English = "en";
    public const string Hungarian = "hu";

    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> TranslationSource = new(() => new()
    {
        [Hungarian] = Hu,
        [English] = En,
    });

    private static Dictionary<string, Dictionary<string, string>> Translations => TranslationSource.Value;

    private static string? _language;

    public static string Language
    {
        get => ResolveLanguage(_language ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        set => _language = ResolveLanguage(value);
    }

    public static IReadOnlyList<string> AvailableLanguages { get; } = new[] { English, Hungarian };

    public static string GetDisplayName(string langCode) => NormalizeLanguageCode(langCode) switch
    {
        Hungarian => "Magyar",
        English => "English",
        _ => langCode,
    };

    public static string Get(string key)
    {
        if (Translations.TryGetValue(Language, out var dict) && dict.TryGetValue(key, out var value))
            return value;
        if (Translations.TryGetValue(English, out var fallback) && fallback.TryGetValue(key, out var fb))
            return fb;
        return key;
    }

    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, format, args);

    private static string ResolveLanguage(string? langCode)
    {
        var normalized = NormalizeLanguageCode(langCode);
        return Translations.ContainsKey(normalized) ? normalized : English;
    }

    private static string NormalizeLanguageCode(string? langCode)
    {
        if (string.IsNullOrWhiteSpace(langCode)) return string.Empty;

        var normalized = langCode.Trim().ToLowerInvariant();
        var dash = normalized.IndexOf('-');
        return dash > 0 ? normalized[..dash] : normalized;
    }

    public static string BootstrapEnroller_InvalidBootstrapDatSkipped => Get(nameof(BootstrapEnroller_InvalidBootstrapDatSkipped));
    public static string BootstrapEnroller_IncompleteBootstrapBlobUrlToken => Get(nameof(BootstrapEnroller_IncompleteBootstrapBlobUrlToken));
    public static string BootstrapEnroller_UsageRemoteAgentBootstrapBlob => Get(nameof(BootstrapEnroller_UsageRemoteAgentBootstrapBlob));
    public static string BootstrapEnroller_InvalidBootstrapBlob => Get(nameof(BootstrapEnroller_InvalidBootstrapBlob));
    public static string BootstrapEnroller_IncompleteBootstrapBlobUrlToken_2 => Get(nameof(BootstrapEnroller_IncompleteBootstrapBlobUrlToken_2));
    public static string BootstrapEnroller_BootstrapDatWrittenToThe => Get(nameof(BootstrapEnroller_BootstrapDatWrittenToThe));
    public static string BootstrapEnroller_BootstrapSelfEnrollError => Get(nameof(BootstrapEnroller_BootstrapSelfEnrollError));
    public static string BootstrapEnroller_BootstrapSelfEnrollFailed => Get(nameof(BootstrapEnroller_BootstrapSelfEnrollFailed));
    public static string BootstrapEnroller_BootstrapSelfEnroll => Get(nameof(BootstrapEnroller_BootstrapSelfEnroll));
    public static string BootstrapEnroller_BootstrapSelfEnrollOK => Get(nameof(BootstrapEnroller_BootstrapSelfEnrollOK));
    public static string BrokerService_ConsoleBrokerStartingPipePipe => Get(nameof(BrokerService_ConsoleBrokerStartingPipePipe));
    public static string BrokerService_BrokerPipeCreationFailedRetrying => Get(nameof(BrokerService_BrokerPipeCreationFailedRetrying));
    public static string BrokerService_BrokerAcceptError => Get(nameof(BrokerService_BrokerAcceptError));
    public static string BrokerService_BrokerClientConnected => Get(nameof(BrokerService_BrokerClientConnected));
    public static string BrokerService_BrokerForwardOKBastionRemote => Get(nameof(BrokerService_BrokerForwardOKBastionRemote));
    public static string BrokerService_BrokerForwardFAILEDPortSee => Get(nameof(BrokerService_BrokerForwardFAILEDPortSee));
    public static string BrokerService_BrokerRequestedPortIsNot => Get(nameof(BrokerService_BrokerRequestedPortIsNot));
    public static string BrokerService_BrokerHandlerError => Get(nameof(BrokerService_BrokerHandlerError));
    public static string BrokerService_BrokerClientDisconnectedForwardsClosed => Get(nameof(BrokerService_BrokerClientDisconnectedForwardsClosed));
    public static string CertHelper_NoClientCertificateThumbprintConfigured => Get(nameof(CertHelper_NoClientCertificateThumbprintConfigured));
    public static string CertHelper_ClientCertificateWithThumbprintWas => Get(nameof(CertHelper_ClientCertificateWithThumbprintWas));
    public static string CertHelper_ClientCertificatePFXNotFound => Get(nameof(CertHelper_ClientCertificatePFXNotFound));
    public static string CertHelper_DPAPIProtectedPFXNotFound => Get(nameof(CertHelper_DPAPIProtectedPFXNotFound));
    public static string CommandChannelService_NoCommandChannelURLConfigured => Get(nameof(CommandChannelService_NoCommandChannelURLConfigured));
    public static string CommandChannelService_CommandChannelErrorReconnectingIn => Get(nameof(CommandChannelService_CommandChannelErrorReconnectingIn));
    public static string CommandChannelService_ConnectingToCommandChannelUrl => Get(nameof(CommandChannelService_ConnectingToCommandChannelUrl));
    public static string CommandChannelService_CommandChannelIsLive => Get(nameof(CommandChannelService_CommandChannelIsLive));
    public static string CommandChannelService_UnparseableCommandMessageDiscarded => Get(nameof(CommandChannelService_UnparseableCommandMessageDiscarded));
    public static string CommandChannelService_PingReceived => Get(nameof(CommandChannelService_PingReceived));
    public static string CommandChannelService_AuthenticatedCommandReceivedType => Get(nameof(CommandChannelService_AuthenticatedCommandReceivedType));
    public static string CommandVerifier_NoCommandSigningPublicKeyConfiguredAllCommands => Get(nameof(CommandVerifier_NoCommandSigningPublicKeyConfiguredAllCommands));
    public static string CommandVerifier_CommandWithoutNonceOrSignature => Get(nameof(CommandVerifier_CommandWithoutNonceOrSignature));
    public static string CommandVerifier_CommandTimestampOutsideWindowAge => Get(nameof(CommandVerifier_CommandTimestampOutsideWindowAge));
    public static string CommandVerifier_CommandSignatureIsInvalidDiscarded => Get(nameof(CommandVerifier_CommandSignatureIsInvalidDiscarded));
    public static string CommandVerifier_CommandNonceAlreadySeenReplay => Get(nameof(CommandVerifier_CommandNonceAlreadySeenReplay));
    public static string EnrollCommand_SshKeygenFailedSSHKey => Get(nameof(EnrollCommand_SshKeygenFailedSSHKey));
    public static string HeartbeatService_HeartbeatWriteFailed => Get(nameof(HeartbeatService_HeartbeatWriteFailed));
    public static string HelperUpdateWatcher_EmptyUpdateUpdaterReadyNo => Get(nameof(HelperUpdateWatcher_EmptyUpdateUpdaterReadyNo));
    public static string HelperUpdateWatcher_HelperUpdateDetectedReplacingTarget => Get(nameof(HelperUpdateWatcher_HelperUpdateDetectedReplacingTarget));
    public static string HelperUpdateWatcher_CouldNotReplaceTheHelper => Get(nameof(HelperUpdateWatcher_CouldNotReplaceTheHelper));
    public static string HelperUpdateWatcher_HelperUpdatedRemoteAgentUpdaterRestarted => Get(nameof(HelperUpdateWatcher_HelperUpdatedRemoteAgentUpdaterRestarted));
    public static string HelperUpdateWatcher_HelperReplacementFailed => Get(nameof(HelperUpdateWatcher_HelperReplacementFailed));
    public static string ServiceControl_CouldNotDetermineExecutablePath => Get(nameof(ServiceControl_CouldNotDetermineExecutablePath));
    public static string ServiceControl_RemoteAppClientRemoteAccessAgent => Get(nameof(ServiceControl_RemoteAppClientRemoteAccessAgent));
    public static string ServiceControl_ServiceUpdatedAndStarted => Get(nameof(ServiceControl_ServiceUpdatedAndStarted));
    public static string ServiceControl_CouldNotCreateServiceAdmin => Get(nameof(ServiceControl_CouldNotCreateServiceAdmin));
    public static string ServiceControl_ServiceInstalledAndStarted => Get(nameof(ServiceControl_ServiceInstalledAndStarted));
    public static string ServiceControl_ServiceRemoved => Get(nameof(ServiceControl_ServiceRemoved));
    public static string ServiceControl_CouldNotDeleteService => Get(nameof(ServiceControl_CouldNotDeleteService));
    public static string ServiceControl_RemoteAgentUpdaterExeIsNot => Get(nameof(ServiceControl_RemoteAgentUpdaterExeIsNot));
    public static string SshLocalForward_SshLForwardWasNot => Get(nameof(SshLocalForward_SshLForwardWasNot));
    public static string SshLocalForward_SshLForwardWasNot_2 => Get(nameof(SshLocalForward_SshLForwardWasNot_2));
    public static string SshLocalForward_ErrorWhileStoppingForward => Get(nameof(SshLocalForward_ErrorWhileStoppingForward));
    public static string SshReverseTunnel_TunnelAlreadyRunningSkippingNew => Get(nameof(SshReverseTunnel_TunnelAlreadyRunningSkippingNew));
    public static string SshReverseTunnel_StartingReverseTunnelBastionHost => Get(nameof(SshReverseTunnel_StartingReverseTunnelBastionHost));
    public static string SshReverseTunnel_ErrorWhileStoppingTunnel => Get(nameof(SshReverseTunnel_ErrorWhileStoppingTunnel));
    public static string StatusPipeService_StatusPipeCreationFailedRetrying => Get(nameof(StatusPipeService_StatusPipeCreationFailedRetrying));
    public static string StatusPipeService_StatusPipeWriteError => Get(nameof(StatusPipeService_StatusPipeWriteError));
    public static string StatusPipeService_StatusPipeStartingPipePipe => Get(nameof(StatusPipeService_StatusPipeStartingPipePipe));
    public static string TelemetryService_NoTelemetryURLConfiguredService => Get(nameof(TelemetryService_NoTelemetryURLConfiguredService));
    public static string TelemetryService_TelemetrySent => Get(nameof(TelemetryService_TelemetrySent));
    public static string TelemetryService_TelemetryRejectedHTTPCode => Get(nameof(TelemetryService_TelemetryRejectedHTTPCode));
    public static string TelemetryService_TelemetriaSendingFailed => Get(nameof(TelemetryService_TelemetriaSendingFailed));
    public static string TunnelOrchestratorService_TunnelCommandProcessingFailedType => Get(nameof(TunnelOrchestratorService_TunnelCommandProcessingFailedType));
    public static string TunnelOrchestratorService_RemoteAccessTunnelDENIEDThis => Get(nameof(TunnelOrchestratorService_RemoteAccessTunnelDENIEDThis));
    public static string TunnelOrchestratorService_FileTransferDENIEDThis => Get(nameof(TunnelOrchestratorService_FileTransferDENIEDThis));
    public static string TunnelOrchestratorService_OpenTunnelDeniedThisDevice => Get(nameof(TunnelOrchestratorService_OpenTunnelDeniedThisDevice));
    public static string TunnelOrchestratorService_OpenTunnelWithInvalidRemote => Get(nameof(TunnelOrchestratorService_OpenTunnelWithInvalidRemote));
    public static string TunnelOrchestratorService_RemoteAccessDENIEDNoSigned => Get(nameof(TunnelOrchestratorService_RemoteAccessDENIEDNoSigned));
    public static string TunnelOrchestratorService_OpenTunnelDeniedNoActive => Get(nameof(TunnelOrchestratorService_OpenTunnelDeniedNoActive));
    public static string TunnelOrchestratorService_RemoteAccess => Get(nameof(TunnelOrchestratorService_RemoteAccess));
    public static string TunnelOrchestratorService_AnAdministratorWantsToConnect => Get(nameof(TunnelOrchestratorService_AnAdministratorWantsToConnect));
    public static string TunnelOrchestratorService_AvailabilityQuestion => Get(nameof(TunnelOrchestratorService_AvailabilityQuestion));
    public static string TunnelOrchestratorService_ThankYouTitle => Get(nameof(TunnelOrchestratorService_ThankYouTitle));
    public static string TunnelOrchestratorService_ThankYouBody => Get(nameof(TunnelOrchestratorService_ThankYouBody));
    public static string TunnelOrchestratorService_MessageFromTitle => Get(nameof(TunnelOrchestratorService_MessageFromTitle));
    public static string PowerControl_RestartComment => Get(nameof(PowerControl_RestartComment));
    public static string TunnelOrchestratorService_RemoteAccessALLOWEDByThe => Get(nameof(TunnelOrchestratorService_RemoteAccessALLOWEDByThe));
    public static string TunnelOrchestratorService_RemoteAccessTheUserDid => Get(nameof(TunnelOrchestratorService_RemoteAccessTheUserDid));
    public static string TunnelOrchestratorService_OpenTunnelConsentTimeout => Get(nameof(TunnelOrchestratorService_OpenTunnelConsentTimeout));
    public static string TunnelOrchestratorService_RemoteAccessDENIEDByThe => Get(nameof(TunnelOrchestratorService_RemoteAccessDENIEDByThe));
    public static string TunnelOrchestratorService_OpenTunnelDeniedConsentOutcome => Get(nameof(TunnelOrchestratorService_OpenTunnelDeniedConsentOutcome));
    public static string TunnelOrchestratorService_TunnelIdleTimeoutIdleS => Get(nameof(TunnelOrchestratorService_TunnelIdleTimeoutIdleS));
    public static string TunnelOrchestratorService_UnknownCommandType => Get(nameof(TunnelOrchestratorService_UnknownCommandType));
    public static string UpdateInstaller_UpdateCommandWithoutURLHash => Get(nameof(UpdateInstaller_UpdateCommandWithoutURLHash));
    public static string UpdateInstaller_CouldNotDetermineUpdaterExe => Get(nameof(UpdateInstaller_CouldNotDetermineUpdaterExe));
    public static string UpdateInstaller_DownloadingUpdateTargetVersionUrl => Get(nameof(UpdateInstaller_DownloadingUpdateTargetVersionUrl));
    public static string UpdateInstaller_UpdateHashMISMATCHExpectedExpected => Get(nameof(UpdateInstaller_UpdateHashMISMATCHExpectedExpected));
    public static string UpdateInstaller_UpdateStagingReadyVersionReplacement => Get(nameof(UpdateInstaller_UpdateStagingReadyVersionReplacement));
    public static string UpdateInstaller_DownloadingTightVNCUpdateVersionUrl => Get(nameof(UpdateInstaller_DownloadingTightVNCUpdateVersionUrl));
    public static string UpdateInstaller_TightVNCUpdateHashMISMATCHExpected => Get(nameof(UpdateInstaller_TightVNCUpdateHashMISMATCHExpected));
    public static string UpdateInstaller_TightVNCUpdatedVersion => Get(nameof(UpdateInstaller_TightVNCUpdatedVersion));
    public static string UpdateInstaller_TightVNCMsiexecExitCodeCode => Get(nameof(UpdateInstaller_TightVNCMsiexecExitCodeCode));
    public static string UpdateInstaller_TightVNCUpdateFailed => Get(nameof(UpdateInstaller_TightVNCUpdateFailed));
    public static string UpdateInstaller_CouldNotDetermineRemoteClientExe => Get(nameof(UpdateInstaller_CouldNotDetermineRemoteClientExe));
    public static string UpdateInstaller_DownloadingClientUpdateVersionUrl => Get(nameof(UpdateInstaller_DownloadingClientUpdateVersionUrl));
    public static string UpdateInstaller_ClientUpdateHashMISMATCHExpected => Get(nameof(UpdateInstaller_ClientUpdateHashMISMATCHExpected));
    public static string UpdateInstaller_ConsoleClientUpdatedVersion => Get(nameof(UpdateInstaller_ConsoleClientUpdatedVersion));
    public static string UpdateInstaller_CouldNotReplaceRemoteClientExe => Get(nameof(UpdateInstaller_CouldNotReplaceRemoteClientExe));
    public static string UpdateInstaller_ClientUpdateFailed => Get(nameof(UpdateInstaller_ClientUpdateFailed));
    public static string UpdateInstaller_KilledRunningRemoteClientForUpdate => Get(nameof(UpdateInstaller_KilledRunningRemoteClientForUpdate));
    public static string UpdateInstaller_CouldNotKillRemoteClientPID => Get(nameof(UpdateInstaller_CouldNotKillRemoteClientPID));
    public static string UpdateInstaller_UpdateFailed => Get(nameof(UpdateInstaller_UpdateFailed));
    public static string VncLock_RemoteAccessVNCIsLOCALLY => Get(nameof(VncLock_RemoteAccessVNCIsLOCALLY));
    public static string VncLock_VNCLocallyDISABLEDTvnserverStopped => Get(nameof(VncLock_VNCLocallyDISABLEDTvnserverStopped));
    public static string VncLock_RemoteAccessVNCUnlockedOn => Get(nameof(VncLock_RemoteAccessVNCUnlockedOn));
    public static string VncLock_VNCUNLOCKEDTvnserverAutoStarted => Get(nameof(VncLock_VNCUNLOCKEDTvnserverAutoStarted));
    public static string VncLock_AdminSYSTEMRightsRequired => Get(nameof(VncLock_AdminSYSTEMRightsRequired));
    public static string VncLock_Error => Get(nameof(VncLock_Error));
    public static string FileLock_FileTransferLOCALLYDisabled => Get(nameof(FileLock_FileTransferLOCALLYDisabled));
    public static string FileLock_FileTransferLOCALLYEnabled => Get(nameof(FileLock_FileTransferLOCALLYEnabled));
    public static string VncProvisioner_TightVNCInstalledAndConfigured => Get(nameof(VncProvisioner_TightVNCInstalledAndConfigured));
    public static string VncProvisioner_TightVNCAlreadyInstalledConfigurationUpdated => Get(nameof(VncProvisioner_TightVNCAlreadyInstalledConfigurationUpdated));
    public static string VncProvisioner_Password => Get(nameof(VncProvisioner_Password));
    public static string VncProvisioner_InsufficientPrivilegesAdminSYSTEMRequired => Get(nameof(VncProvisioner_InsufficientPrivilegesAdminSYSTEMRequired));
    public static string VncProvisioner_TightVNCMSINotFound => Get(nameof(VncProvisioner_TightVNCMSINotFound));
    public static string VncProvisioner_MsiexecExitCode => Get(nameof(VncProvisioner_MsiexecExitCode));
    public static string VncProvisioner_VNCProvisioningError => Get(nameof(VncProvisioner_VNCProvisioningError));
    public static string VncProvisioningService_VNCProvisionedPerDevicePassword => Get(nameof(VncProvisioningService_VNCProvisionedPerDevicePassword));
    public static string VncProvisioningService_VNCPasswordReportFailed => Get(nameof(VncProvisioningService_VNCPasswordReportFailed));
    public static string VncProvisioningService_VNCPasswordReportedToThe => Get(nameof(VncProvisioningService_VNCPasswordReportedToThe));
    public static string VncProvisioningService_VNCPasswordReportRejectedHTTP => Get(nameof(VncProvisioningService_VNCPasswordReportRejectedHTTP));
    public static string VncProvisioningService_VNCIsLocallyDisabledProvisioning => Get(nameof(VncProvisioningService_VNCIsLocallyDisabledProvisioning));
    public static string VncProvisioningService_VNCProvisioningSkippedAdminSYSTEM => Get(nameof(VncProvisioningService_VNCProvisioningSkippedAdminSYSTEM));
}
