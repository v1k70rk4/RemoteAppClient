-- RemoteServer 2.3.0 — TPM telemetry, device key in the TPM and certificate renewal (ADR-0003).
--
-- Devices gain the TPM telemetry the agent reports (present, version, manufacturer, ready for keys, attestation,
-- vulnerable firmware; NULL = unknown, an older agent, never "no TPM").
--
-- Devices gain: where their key lives as the agent reports it (KeyProvider: tpm / software / file), the
-- certificate's expiry (CertNotAfter), a certificate issued for a re-key that the device has not confirmed yet
-- (PendingCertThumbprint / PendingCertUntil) and the retired one still accepted for a few minutes after a
-- confirmed switch (PreviousCertThumbprint / PreviousCertValidUntil). All NULL for devices that never re-keyed.
--
-- Idempotent (IF NOT EXISTS) — safe to run repeatedly. Apply via the in-app
-- "Szerver frissítés → SQL kiválasztása" upload (or racctl update --sql), or manually against the database.

ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmPresent` tinyint(1) NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmVersion` longtext CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmManufacturer` longtext CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmReady` tinyint(1) NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmAttestation` tinyint(1) NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmVulnerableFirmware` tinyint(1) NULL;

ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `CertNotAfter` datetime(6) NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `PendingCertThumbprint` longtext CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `PendingCertUntil` datetime(6) NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `PreviousCertThumbprint` longtext CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `PreviousCertValidUntil` datetime(6) NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `KeyProvider` longtext CHARACTER SET utf8mb4 NULL;

-- Lost-key requests: a device whose TPM lost its key asks for a new certificate without one; an administrator
-- approves or rejects it in the console (ADR-0003, recovery).
CREATE TABLE IF NOT EXISTS `RekeyRequests` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `DeviceKey` char(36) COLLATE ascii_general_ci NOT NULL,
    `Hostname` longtext CHARACTER SET utf8mb4 NOT NULL,
    `SourceIp` longtext CHARACTER SET utf8mb4 NULL,
    `Csr` longtext CHARACTER SET utf8mb4 NOT NULL,
    `KeyProvider` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
    `KeyFingerprint` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    `TokenHash` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `ExpiresAt` datetime(6) NOT NULL,
    `State` varchar(16) CHARACTER SET utf8mb4 NOT NULL,
    `DecidedAt` datetime(6) NULL,
    `DecidedBy` longtext CHARACTER SET utf8mb4 NULL,
    `CertificatePem` longtext CHARACTER SET utf8mb4 NULL,
    `CertNotAfter` datetime(6) NULL,
    CONSTRAINT `PK_RekeyRequests` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;
CREATE INDEX IF NOT EXISTS `IX_RekeyRequests_DeviceKey_State` ON `RekeyRequests` (`DeviceKey`, `State`);
ALTER TABLE `RekeyRequests` ADD COLUMN IF NOT EXISTS `KeyFingerprint` varchar(32) CHARACTER SET utf8mb4 NOT NULL DEFAULT '';
