using System.Collections.Generic;
using System.Globalization;

namespace RemoteServer.Localization;

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

    /// <summary>Lookup in a SPECIFIC language (e.g. the requesting client's), independent of the process language.</summary>
    public static string Get(string key, string language)
    {
        var lang = ResolveLanguage(language);
        if (Translations.TryGetValue(lang, out var dict) && dict.TryGetValue(key, out var value))
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

    public static string CertificateAuthority_CAFilesAreMissing => Get(nameof(CertificateAuthority_CAFilesAreMissing));
    public static string CertificateAuthority_GenerateThemDuringProvisioningDeploy => Get(nameof(CertificateAuthority_GenerateThemDuringProvisioningDeploy));
    public static string CertificateAuthority_CALoadedSubject => Get(nameof(CertificateAuthority_CALoadedSubject));
    public static string CommandService_UnknownDeviceCommandDiscardedDevice => Get(nameof(CommandService_UnknownDeviceCommandDiscardedDevice));
    public static string CommandService_CommandDeliveredToDeviceType => Get(nameof(CommandService_CommandDeliveredToDeviceType));
    public static string CommandService_DeviceOfflineCommandRemainsQueued => Get(nameof(CommandService_DeviceOfflineCommandRemainsQueued));
    public static string CommandSigner_CommandSigningPrivateKeyNot => Get(nameof(CommandSigner_CommandSigningPrivateKeyNot));
    public static string EmailSender_MissingRecipient => Get(nameof(EmailSender_MissingRecipient));
    public static string EmailSender_NoEmailSettings => Get(nameof(EmailSender_NoEmailSettings));
    public static string EmailSender_NoActiveEmailProviderConfigured => Get(nameof(EmailSender_NoActiveEmailProviderConfigured));
    public static string EmailSender_EMailDeliveryErrorProvider => Get(nameof(EmailSender_EMailDeliveryErrorProvider));
    public static string EmailSender_MissingSMTPHost => Get(nameof(EmailSender_MissingSMTPHost));
    public static string EmailSender_MissingSenderSmtpFromSmtpUser => Get(nameof(EmailSender_MissingSenderSmtpFromSmtpUser));
    public static string EmailSender_MissingGraphSettingsTenantClient => Get(nameof(EmailSender_MissingGraphSettingsTenantClient));
    public static string EmailSender_MissingGraphClientSecret => Get(nameof(EmailSender_MissingGraphClientSecret));
    public static string EmailSender_TokenResponseDoesNotContain => Get(nameof(EmailSender_TokenResponseDoesNotContain));
    public static string EmailSender_TokenError => Get(nameof(EmailSender_TokenError));
    public static string EmailSender_SendMailError => Get(nameof(EmailSender_SendMailError));
    public static string EnrollmentService_CSRSigningFailedDeviceDevice => Get(nameof(EnrollmentService_CSRSigningFailedDeviceDevice));
    public static string EnrollmentService_DeviceEnrolledDeviceHost => Get(nameof(EnrollmentService_DeviceEnrolledDeviceHost));
    public static string MsiBuilder_MSIBuiltFileSizeBytes => Get(nameof(MsiBuilder_MSIBuiltFileSizeBytes));
    public static string MsiBuilder_MSIBuildError => Get(nameof(MsiBuilder_MSIBuildError));
    public static string MsiBuilder_MSISigned => Get(nameof(MsiBuilder_MSISigned));
    public static string MsiBuilder_MSISigningSkippedFailedOsslsigncode => Get(nameof(MsiBuilder_MSISigningSkippedFailedOsslsigncode));
    public static string MsiBuilder_MSISigningErrorSkipped => Get(nameof(MsiBuilder_MSISigningErrorSkipped));
    public static string MsiBuilder_WixlErrorRcCodeOutput => Get(nameof(MsiBuilder_WixlErrorRcCodeOutput));
    public static string Program_UnknownUser => Get(nameof(Program_UnknownUser));
    public static string Program_InvalidPassword => Get(nameof(Program_InvalidPassword));
    public static string Program_InvalidTOTP => Get(nameof(Program_InvalidTOTP));
    public static string Program_UnknownDevice => Get(nameof(Program_UnknownDevice));
    public static string Program_InvalidOrExpiredToken => Get(nameof(Program_InvalidOrExpiredToken));
    public static string Program_AgentConnectedDevice => Get(nameof(Program_AgentConnectedDevice));
    public static string Program_WSClosedDevice => Get(nameof(Program_WSClosedDevice));
    public static string Program_AgentDisconnectedDevice => Get(nameof(Program_AgentDisconnectedDevice));
    public static string Program_Devices => Get(nameof(Program_Devices));
    public static string Program_SetServerPublicUrlOrProvide => Get(nameof(Program_SetServerPublicUrlOrProvide));
    public static string Program_SmtpPasswordChanged => Get(nameof(Program_SmtpPasswordChanged));
    public static string Program_GraphSecretChanged => Get(nameof(Program_GraphSecretChanged));
    public static string Program_ThisIsATestEmail => Get(nameof(Program_ThisIsATestEmail));
    public static string Program_BOOTSTRAPAdminCreatedUsernameAdmin => Get(nameof(Program_BOOTSTRAPAdminCreatedUsernameAdmin));
    public static string Program_AuditWriteErrorAction => Get(nameof(Program_AuditWriteErrorAction));
    public static string Program_FailedAttemptsLast => Get(nameof(Program_FailedAttemptsLast));
    public static string Program_ThereWereFailedSignIn => Get(nameof(Program_ThereWereFailedSignIn));
    public static string Program_LastAttemptedUsernameSourceIP => Get(nameof(Program_LastAttemptedUsernameSourceIP));
    public static string Program_UnlockInTheClientGo => Get(nameof(Program_UnlockInTheClientGo));
    public static string Program_RemoteAppClientDeviceSignInLocked => Get(nameof(Program_RemoteAppClientDeviceSignInLocked));
    public static string Program_PasswordRecoveryTokenForAccount => Get(nameof(Program_PasswordRecoveryTokenForAccount));
    public static string Program_TheTokenIsValidFor => Get(nameof(Program_TheTokenIsValidFor));
    public static string Program_IfYouDidNotRequest => Get(nameof(Program_IfYouDidNotRequest));
    public static string Program_RemoteAppClientPasswordRecoveryToken => Get(nameof(Program_RemoteAppClientPasswordRecoveryToken));
    public static string Program_AccessResultDeviceOutcomeNonce => Get(nameof(Program_AccessResultDeviceOutcomeNonce));
    public static string Program_AuditWriteAccessFailed => Get(nameof(Program_AuditWriteAccessFailed));
    public static string Program_UnparseableAgentMessageDevice => Get(nameof(Program_UnparseableAgentMessageDevice));
    public static string Program_EmailDidNotMatch => Get(nameof(Program_EmailDidNotMatch));
    public static string Program_Error => Get(nameof(Program_Error));
    public static string Program_MintBlobFirstRunCheck => Get(nameof(Program_MintBlobFirstRunCheck));
    public static string Program_MintBlobDbReachableSchemaApplied => Get(nameof(Program_MintBlobDbReachableSchemaApplied));
    public static string Program_MintBlobRunSchemaAndCheckConnection => Get(nameof(Program_MintBlobRunSchemaAndCheckConnection));
    public static string Program_MintBlobAdminExists => Get(nameof(Program_MintBlobAdminExists));
    public static string Program_MintBlobCommandSigningKey => Get(nameof(Program_MintBlobCommandSigningKey));
    public static string Program_MintBlobCaCertAndKey => Get(nameof(Program_MintBlobCaCertAndKey));
    public static string Program_MintBlobGenerateCaSeeFirstRun => Get(nameof(Program_MintBlobGenerateCaSeeFirstRun));
    public static string Program_MintBlobEmpty => Get(nameof(Program_MintBlobEmpty));
    public static string Program_MintBlobSetServerPublicUrl => Get(nameof(Program_MintBlobSetServerPublicUrl));
    public static string Program_MintBlobBastionHostAndHostKey => Get(nameof(Program_MintBlobBastionHostAndHostKey));
    public static string Program_MintBlobSecretKey => Get(nameof(Program_MintBlobSecretKey));
    public static string Program_MintBlobMissingRequiredItems => Get(nameof(Program_MintBlobMissingRequiredItems));
    public static string Program_MintBlobHeader => Get(nameof(Program_MintBlobHeader));
    public static string Program_MintBlobUsage => Get(nameof(Program_MintBlobUsage));
    public static string Program_MintBlobGenerationError => Get(nameof(Program_MintBlobGenerationError));
    public static string SecretExpiryWatcher_SecretExpiryCheckError => Get(nameof(SecretExpiryWatcher_SecretExpiryCheckError));
    public static string SecretExpiryWatcher_TheRemoteAppClientEmailDeliveryGraph => Get(nameof(SecretExpiryWatcher_TheRemoteAppClientEmailDeliveryGraph));
    public static string SecretExpiryWatcher_TheRemoteAppClientEmailDeliveryGraph_2 => Get(nameof(SecretExpiryWatcher_TheRemoteAppClientEmailDeliveryGraph_2));
    public static string SecretExpiryWatcher_RemoteAppClientEmailDeliverySecretExpires => Get(nameof(SecretExpiryWatcher_RemoteAppClientEmailDeliverySecretExpires));
    public static string SecretExpiryWatcher_SecretExpiryWarningSentTo => Get(nameof(SecretExpiryWatcher_SecretExpiryWarningSentTo));
    public static string SecretExpiryWatcher_SecretExpiryWarningSendFailed => Get(nameof(SecretExpiryWatcher_SecretExpiryWarningSendFailed));
    public static string SecretProtector_EncryptionKeyIsMissingGenerate => Get(nameof(SecretProtector_EncryptionKeyIsMissingGenerate));
    public static string SecretProtector_EncryptionKeyIsNot32 => Get(nameof(SecretProtector_EncryptionKeyIsNot32));
    public static string SecretProtector_SecretEncryptionKeyLoaded => Get(nameof(SecretProtector_SecretEncryptionKeyLoaded));
    public static string SshCertificateAuthority_SSHCAKeyNotFound => Get(nameof(SshCertificateAuthority_SSHCAKeyNotFound));
    public static string SshCertificateAuthority_SshKeygenSigningFailedDevice => Get(nameof(SshCertificateAuthority_SshKeygenSigningFailedDevice));
}
