-- RemoteServer 2.2.7 — TPM telemetry: which devices have a TPM that could hold the device key, before the key
-- moves into it.
--
-- The agent reads `tpmtool getdeviceinformation` (every six hours) and reports: a TPM is present, its version
-- and manufacturer id, whether it is ready for storage (keys can be created in it) and for attestation, and
-- whether Windows flags its firmware as vulnerable. All NULL until an agent that reports them checks in, and
-- for older agents: NULL means unknown, never "no TPM".
--
-- Idempotent (IF NOT EXISTS) — safe to run repeatedly. Apply via the in-app
-- "Szerver frissítés → SQL kiválasztása" upload (or racctl update --sql), or manually against the database.

ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmPresent` tinyint(1) NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmVersion` longtext CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmManufacturer` longtext CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmReady` tinyint(1) NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmAttestation` tinyint(1) NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `TpmVulnerableFirmware` tinyint(1) NULL;
