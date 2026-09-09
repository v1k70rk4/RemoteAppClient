-- RemoteServer 2.1.5 — device health: record WHAT is wrong with a device, not just whether it answers.
--
-- A device can be online, green and reporting every minute while the agent silently discards every command
-- it receives — that is exactly how CA-KOKAZSUZSA looked for two hours: telemetry fresh, badge green, and
-- an event log full of "Parancs időbélyege ablakon kívül (88s), eldobva". Nothing on the server or in the
-- console could say so, because nothing had anywhere to say it.
--
-- `Problem` holds a language-neutral code ("clock-skew:+88") that each console renders in its own language;
-- `ProblemSince` keeps the first sighting so the console can say how long it has been broken. Both are NULL
-- while the device is healthy. Clock skew is derived by the server from the telemetry's own CollectedAtUtc,
-- which is NOT signed and therefore still arrives from a device whose every command is being thrown away.
--
-- Idempotent (IF NOT EXISTS) — safe to run repeatedly. Apply via the in-app
-- "Szerver frissítés → SQL kiválasztása" upload, or manually against the prod database.

ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `Problem` longtext CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Devices` ADD COLUMN IF NOT EXISTS `ProblemSince` datetime(6) NULL;
