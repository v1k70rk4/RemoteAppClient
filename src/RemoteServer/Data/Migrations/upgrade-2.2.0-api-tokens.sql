-- RemoteServer 2.2.0 — read-only access tokens for tooling (racctl) and the diagnostics endpoints.
--
-- An admin mints a token for their own account in the console (Server settings → Diagnostics). It opens the
-- server's own log, the health snapshot and the device list to command-line tools without the admin's
-- password or 2FA; everything read with it is attributed to the owner. The raw token is shown once and only
-- its SHA-256 hash is stored here. It is accepted solely on the tunnel-only /admin path.
--
-- Idempotent (IF NOT EXISTS) — safe to run repeatedly. Apply via the in-app
-- "Szerver frissítés → SQL kiválasztása" upload together with the 2.2.0 tar.gz, or manually against the
-- prod database.

CREATE TABLE IF NOT EXISTS `ApiTokens` (
    `Id` char(36) COLLATE ascii_general_ci NOT NULL,
    `UserId` char(36) COLLATE ascii_general_ci NOT NULL,
    `Name` longtext CHARACTER SET utf8mb4 NOT NULL,
    `TokenHash` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `Prefix` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Scope` longtext CHARACTER SET utf8mb4 NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `ExpiresAt` datetime(6) NULL,
    `LastUsedAt` datetime(6) NULL,
    `LastUsedIp` longtext CHARACTER SET utf8mb4 NULL,
    `RevokedAt` datetime(6) NULL,
    CONSTRAINT `PK_ApiTokens` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_ApiTokens_Users_UserId` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;

CREATE UNIQUE INDEX IF NOT EXISTS `IX_ApiTokens_TokenHash` ON `ApiTokens` (`TokenHash`);
CREATE INDEX IF NOT EXISTS `IX_ApiTokens_UserId` ON `ApiTokens` (`UserId`);
