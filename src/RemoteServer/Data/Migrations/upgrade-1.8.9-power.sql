-- RemoteServer 1.8.9 — power telemetry (battery + sleep) columns on the Devices table.
-- Idempotent (ADD COLUMN IF NOT EXISTS) — safe to run repeatedly. Apply via the in-app
-- "Szerver frissítés → SQL kiválasztása" upload, or manually against the prod database.
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `AcOnline`       tinyint(1) NOT NULL DEFAULT 0;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `BatteryPercent` int        NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `SleepAcMinutes` int        NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `SleepDcMinutes` int        NULL;
