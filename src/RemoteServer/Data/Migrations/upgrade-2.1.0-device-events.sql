-- RemoteServer 2.1.0 — device history: replace the telemetry snapshot table with an event log.
--
-- The old `DeviceTelemetry` stored the full payload once a minute per device and reached 755 MB across
-- fifteen devices in three months — while no endpoint and no client ever read a single row of it. Current
-- values already live denormalised on `Devices`; only transitions are worth keeping, so `DeviceEvents`
-- records just liveness changes (online / flaky / not-controllable / offline) and IP changes, pruned at
-- 90 days. A device that stays put and stays online now writes nothing at all.
--
-- Idempotent (IF NOT EXISTS / IF EXISTS) — safe to run repeatedly. Apply via the in-app
-- "Szerver frissítés → SQL kiválasztása" upload, or manually against the prod database.

CREATE TABLE IF NOT EXISTS `DeviceEvents` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `DeviceId` char(36) COLLATE ascii_general_ci NOT NULL,
    `At` datetime(6) NOT NULL,
    `Kind` longtext CHARACTER SET utf8mb4 NOT NULL,
    `OldValue` longtext CHARACTER SET utf8mb4 NULL,
    `NewValue` longtext CHARACTER SET utf8mb4 NULL,
    CONSTRAINT `PK_DeviceEvents` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE INDEX IF NOT EXISTS `IX_DeviceEvents_At`           ON `DeviceEvents` (`At`);
CREATE INDEX IF NOT EXISTS `IX_DeviceEvents_DeviceId_At`  ON `DeviceEvents` (`DeviceId`, `At`);

-- Frees the 755 MB. Nothing reads this table, and the server no longer writes it.
DROP TABLE IF EXISTS `DeviceTelemetry`;
