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

    public static string BootstrapEnroller_001 => Get(nameof(BootstrapEnroller_001));
    public static string BootstrapEnroller_002 => Get(nameof(BootstrapEnroller_002));
    public static string BootstrapEnroller_003 => Get(nameof(BootstrapEnroller_003));
    public static string BootstrapEnroller_004 => Get(nameof(BootstrapEnroller_004));
    public static string BootstrapEnroller_005 => Get(nameof(BootstrapEnroller_005));
    public static string BootstrapEnroller_006 => Get(nameof(BootstrapEnroller_006));
    public static string BootstrapEnroller_007 => Get(nameof(BootstrapEnroller_007));
    public static string BootstrapEnroller_008 => Get(nameof(BootstrapEnroller_008));
    public static string BootstrapEnroller_009 => Get(nameof(BootstrapEnroller_009));
    public static string BootstrapEnroller_010 => Get(nameof(BootstrapEnroller_010));
    public static string BrokerService_001 => Get(nameof(BrokerService_001));
    public static string BrokerService_002 => Get(nameof(BrokerService_002));
    public static string BrokerService_003 => Get(nameof(BrokerService_003));
    public static string BrokerService_004 => Get(nameof(BrokerService_004));
    public static string BrokerService_005 => Get(nameof(BrokerService_005));
    public static string BrokerService_006 => Get(nameof(BrokerService_006));
    public static string BrokerService_007 => Get(nameof(BrokerService_007));
    public static string BrokerService_008 => Get(nameof(BrokerService_008));
    public static string BrokerService_009 => Get(nameof(BrokerService_009));
    public static string CertHelper_001 => Get(nameof(CertHelper_001));
    public static string CertHelper_002 => Get(nameof(CertHelper_002));
    public static string CertHelper_003 => Get(nameof(CertHelper_003));
    public static string CertHelper_004 => Get(nameof(CertHelper_004));
    public static string CommandChannelService_001 => Get(nameof(CommandChannelService_001));
    public static string CommandChannelService_002 => Get(nameof(CommandChannelService_002));
    public static string CommandChannelService_003 => Get(nameof(CommandChannelService_003));
    public static string CommandChannelService_004 => Get(nameof(CommandChannelService_004));
    public static string CommandChannelService_005 => Get(nameof(CommandChannelService_005));
    public static string CommandChannelService_006 => Get(nameof(CommandChannelService_006));
    public static string CommandChannelService_007 => Get(nameof(CommandChannelService_007));
    public static string CommandVerifier_001 => Get(nameof(CommandVerifier_001));
    public static string CommandVerifier_002 => Get(nameof(CommandVerifier_002));
    public static string CommandVerifier_003 => Get(nameof(CommandVerifier_003));
    public static string CommandVerifier_004 => Get(nameof(CommandVerifier_004));
    public static string CommandVerifier_005 => Get(nameof(CommandVerifier_005));
    public static string EnrollCommand_001 => Get(nameof(EnrollCommand_001));
    public static string HeartbeatService_001 => Get(nameof(HeartbeatService_001));
    public static string HelperUpdateWatcher_001 => Get(nameof(HelperUpdateWatcher_001));
    public static string HelperUpdateWatcher_002 => Get(nameof(HelperUpdateWatcher_002));
    public static string HelperUpdateWatcher_003 => Get(nameof(HelperUpdateWatcher_003));
    public static string HelperUpdateWatcher_004 => Get(nameof(HelperUpdateWatcher_004));
    public static string HelperUpdateWatcher_005 => Get(nameof(HelperUpdateWatcher_005));
    public static string ServiceControl_001 => Get(nameof(ServiceControl_001));
    public static string ServiceControl_002 => Get(nameof(ServiceControl_002));
    public static string ServiceControl_003 => Get(nameof(ServiceControl_003));
    public static string ServiceControl_004 => Get(nameof(ServiceControl_004));
    public static string ServiceControl_005 => Get(nameof(ServiceControl_005));
    public static string ServiceControl_006 => Get(nameof(ServiceControl_006));
    public static string ServiceControl_007 => Get(nameof(ServiceControl_007));
    public static string ServiceControl_008 => Get(nameof(ServiceControl_008));
    public static string SshLocalForward_001 => Get(nameof(SshLocalForward_001));
    public static string SshLocalForward_002 => Get(nameof(SshLocalForward_002));
    public static string SshLocalForward_003 => Get(nameof(SshLocalForward_003));
    public static string SshReverseTunnel_001 => Get(nameof(SshReverseTunnel_001));
    public static string SshReverseTunnel_002 => Get(nameof(SshReverseTunnel_002));
    public static string SshReverseTunnel_003 => Get(nameof(SshReverseTunnel_003));
    public static string StatusPipeService_001 => Get(nameof(StatusPipeService_001));
    public static string StatusPipeService_002 => Get(nameof(StatusPipeService_002));
    public static string StatusPipeService_003 => Get(nameof(StatusPipeService_003));
    public static string TelemetryService_001 => Get(nameof(TelemetryService_001));
    public static string TelemetryService_002 => Get(nameof(TelemetryService_002));
    public static string TelemetryService_003 => Get(nameof(TelemetryService_003));
    public static string TelemetryService_004 => Get(nameof(TelemetryService_004));
    public static string TunnelOrchestratorService_001 => Get(nameof(TunnelOrchestratorService_001));
    public static string TunnelOrchestratorService_002 => Get(nameof(TunnelOrchestratorService_002));
    public static string TunnelOrchestratorService_003 => Get(nameof(TunnelOrchestratorService_003));
    public static string TunnelOrchestratorService_004 => Get(nameof(TunnelOrchestratorService_004));
    public static string TunnelOrchestratorService_005 => Get(nameof(TunnelOrchestratorService_005));
    public static string TunnelOrchestratorService_006 => Get(nameof(TunnelOrchestratorService_006));
    public static string TunnelOrchestratorService_007 => Get(nameof(TunnelOrchestratorService_007));
    public static string TunnelOrchestratorService_008 => Get(nameof(TunnelOrchestratorService_008));
    public static string TunnelOrchestratorService_009 => Get(nameof(TunnelOrchestratorService_009));
    public static string TunnelOrchestratorService_010 => Get(nameof(TunnelOrchestratorService_010));
    public static string TunnelOrchestratorService_011 => Get(nameof(TunnelOrchestratorService_011));
    public static string TunnelOrchestratorService_012 => Get(nameof(TunnelOrchestratorService_012));
    public static string TunnelOrchestratorService_013 => Get(nameof(TunnelOrchestratorService_013));
    public static string TunnelOrchestratorService_014 => Get(nameof(TunnelOrchestratorService_014));
    public static string TunnelOrchestratorService_015 => Get(nameof(TunnelOrchestratorService_015));
    public static string UpdateInstaller_001 => Get(nameof(UpdateInstaller_001));
    public static string UpdateInstaller_002 => Get(nameof(UpdateInstaller_002));
    public static string UpdateInstaller_003 => Get(nameof(UpdateInstaller_003));
    public static string UpdateInstaller_004 => Get(nameof(UpdateInstaller_004));
    public static string UpdateInstaller_005 => Get(nameof(UpdateInstaller_005));
    public static string UpdateInstaller_006 => Get(nameof(UpdateInstaller_006));
    public static string UpdateInstaller_007 => Get(nameof(UpdateInstaller_007));
    public static string UpdateInstaller_008 => Get(nameof(UpdateInstaller_008));
    public static string UpdateInstaller_009 => Get(nameof(UpdateInstaller_009));
    public static string UpdateInstaller_010 => Get(nameof(UpdateInstaller_010));
    public static string UpdateInstaller_011 => Get(nameof(UpdateInstaller_011));
    public static string UpdateInstaller_012 => Get(nameof(UpdateInstaller_012));
    public static string UpdateInstaller_013 => Get(nameof(UpdateInstaller_013));
    public static string UpdateInstaller_014 => Get(nameof(UpdateInstaller_014));
    public static string UpdateInstaller_015 => Get(nameof(UpdateInstaller_015));
    public static string UpdateInstaller_016 => Get(nameof(UpdateInstaller_016));
    public static string UpdateInstaller_017 => Get(nameof(UpdateInstaller_017));
    public static string UpdateInstaller_018 => Get(nameof(UpdateInstaller_018));
    public static string UpdateInstaller_019 => Get(nameof(UpdateInstaller_019));
    public static string VncLock_001 => Get(nameof(VncLock_001));
    public static string VncLock_002 => Get(nameof(VncLock_002));
    public static string VncLock_003 => Get(nameof(VncLock_003));
    public static string VncLock_004 => Get(nameof(VncLock_004));
    public static string VncLock_005 => Get(nameof(VncLock_005));
    public static string VncLock_006 => Get(nameof(VncLock_006));
    public static string VncProvisioner_001 => Get(nameof(VncProvisioner_001));
    public static string VncProvisioner_002 => Get(nameof(VncProvisioner_002));
    public static string VncProvisioner_003 => Get(nameof(VncProvisioner_003));
    public static string VncProvisioner_004 => Get(nameof(VncProvisioner_004));
    public static string VncProvisioner_005 => Get(nameof(VncProvisioner_005));
    public static string VncProvisioner_006 => Get(nameof(VncProvisioner_006));
    public static string VncProvisioner_007 => Get(nameof(VncProvisioner_007));
    public static string VncProvisioningService_001 => Get(nameof(VncProvisioningService_001));
    public static string VncProvisioningService_002 => Get(nameof(VncProvisioningService_002));
    public static string VncProvisioningService_003 => Get(nameof(VncProvisioningService_003));
    public static string VncProvisioningService_004 => Get(nameof(VncProvisioningService_004));
    public static string VncProvisioningService_005 => Get(nameof(VncProvisioningService_005));
    public static string VncProvisioningService_006 => Get(nameof(VncProvisioningService_006));
}
