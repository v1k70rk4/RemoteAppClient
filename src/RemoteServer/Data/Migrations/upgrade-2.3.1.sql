-- RemoteServer 2.3.1 — the tunnel (SSH) key in the TPM (ADR-0004).
--
-- Devices gain: where the tunnel key lives as the agent reports it (SshKeyProvider: tpm / software / file), and a
-- rotation in flight (PendingSshPublicKey / PendingSshUntil): the new key the server issued a certificate for but
-- the device has not yet proven at the bastion. All NULL for devices that never rotated this way.
--
-- Idempotent (IF NOT EXISTS) — safe to run repeatedly. Apply via the in-app
-- "Szerver frissítés → SQL kiválasztása" upload (or racctl update --sql), or manually against the database.

ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `SshKeyProvider` varchar(16) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `PendingSshPublicKey` longtext CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `PendingSshUntil` datetime(6) NULL;
