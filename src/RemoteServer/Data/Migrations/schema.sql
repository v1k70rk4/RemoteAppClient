CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (
    `MigrationId` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
    `ProductVersion` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    CONSTRAINT `PK___EFMigrationsHistory` PRIMARY KEY (`MigrationId`)
) CHARACTER SET=utf8mb4;

START TRANSACTION;
DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    ALTER DATABASE CHARACTER SET utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE TABLE `AuditLogs` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `Actor` longtext CHARACTER SET utf8mb4 NOT NULL,
        `Action` longtext CHARACTER SET utf8mb4 NOT NULL,
        `TargetDeviceId` char(36) COLLATE ascii_general_ci NULL,
        `DetailJson` json NULL,
        `Ip` longtext CHARACTER SET utf8mb4 NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_AuditLogs` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE TABLE `Commands` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `DeviceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `Type` longtext CHARACTER SET utf8mb4 NOT NULL,
        `PayloadJson` json NULL,
        `Status` int NOT NULL,
        `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `SentAt` datetime(6) NULL,
        `CompletedAt` datetime(6) NULL,
        `ResultJson` json NULL,
        `Nonce` longtext CHARACTER SET utf8mb4 NULL,
        `Signature` longtext CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_Commands` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE TABLE `DeviceGroups` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` longtext CHARACTER SET utf8mb4 NOT NULL,
        `ConsentRequired` tinyint(1) NOT NULL,
        `UnattendedAllowed` tinyint(1) NOT NULL,
        `Note` longtext CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_DeviceGroups` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE TABLE `DeviceTelemetry` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `DeviceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `CollectedAt` datetime(6) NOT NULL,
        `PayloadJson` json NOT NULL,
        CONSTRAINT `PK_DeviceTelemetry` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE TABLE `EnrollmentTokens` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `TokenHash` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `CreatedByUserId` char(36) COLLATE ascii_general_ci NULL,
        `GroupId` char(36) COLLATE ascii_general_ci NULL,
        `CreatedAt` datetime(6) NOT NULL,
        `ExpiresAt` datetime(6) NULL,
        `MaxUses` int NOT NULL,
        `UseCount` int NOT NULL,
        `UsedAt` datetime(6) NULL,
        `UsedByDeviceId` char(36) COLLATE ascii_general_ci NULL,
        `Note` longtext CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_EnrollmentTokens` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE TABLE `RemoteSessions` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `DeviceId` char(36) COLLATE ascii_general_ci NOT NULL,
        `RemotePort` int NOT NULL,
        `OpenedByUserId` char(36) COLLATE ascii_general_ci NULL,
        `OpenedAt` datetime(6) NOT NULL,
        `ClosedAt` datetime(6) NULL,
        `ConsentState` int NOT NULL,
        CONSTRAINT `PK_RemoteSessions` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE TABLE `Roles` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `Name` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        CONSTRAINT `PK_Roles` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE TABLE `Users` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `Username` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `Email` longtext CHARACTER SET utf8mb4 NULL,
        `PasswordHash` longtext CHARACTER SET utf8mb4 NOT NULL,
        `TotpSecret` longtext CHARACTER SET utf8mb4 NULL,
        `IsActive` tinyint(1) NOT NULL,
        `CreatedAt` datetime(6) NOT NULL,
        CONSTRAINT `PK_Users` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE TABLE `Devices` (
        `Id` char(36) COLLATE ascii_general_ci NOT NULL,
        `DeviceId` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
        `Hostname` longtext CHARACTER SET utf8mb4 NOT NULL,
        `GroupId` char(36) COLLATE ascii_general_ci NULL,
        `Status` int NOT NULL,
        `ConsentRequired` tinyint(1) NULL,
        `CertThumbprint` longtext CHARACTER SET utf8mb4 NULL,
        `SshPublicKey` longtext CHARACTER SET utf8mb4 NULL,
        `VncSecret` longtext CHARACTER SET utf8mb4 NULL,
        `VncSecretUpdatedAt` datetime(6) NULL,
        `AgentVersion` longtext CHARACTER SET utf8mb4 NULL,
        `OsVersion` longtext CHARACTER SET utf8mb4 NULL,
        `LastSeenAt` datetime(6) NULL,
        `EnrolledAt` datetime(6) NOT NULL,
        `Note` longtext CHARACTER SET utf8mb4 NULL,
        CONSTRAINT `PK_Devices` PRIMARY KEY (`Id`),
        CONSTRAINT `FK_Devices_DeviceGroups_GroupId` FOREIGN KEY (`GroupId`) REFERENCES `DeviceGroups` (`Id`) ON DELETE SET NULL
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE TABLE `UserRoles` (
        `UserId` char(36) COLLATE ascii_general_ci NOT NULL,
        `RoleId` char(36) COLLATE ascii_general_ci NOT NULL,
        CONSTRAINT `PK_UserRoles` PRIMARY KEY (`UserId`, `RoleId`),
        CONSTRAINT `FK_UserRoles_Roles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `Roles` (`Id`) ON DELETE CASCADE,
        CONSTRAINT `FK_UserRoles_Users_UserId` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE INDEX `IX_AuditLogs_CreatedAt` ON `AuditLogs` (`CreatedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE INDEX `IX_Commands_DeviceId_Status` ON `Commands` (`DeviceId`, `Status`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE UNIQUE INDEX `IX_Devices_DeviceId` ON `Devices` (`DeviceId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE INDEX `IX_Devices_GroupId` ON `Devices` (`GroupId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE INDEX `IX_Devices_Status` ON `Devices` (`Status`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE INDEX `IX_DeviceTelemetry_DeviceId_CollectedAt` ON `DeviceTelemetry` (`DeviceId`, `CollectedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE UNIQUE INDEX `IX_EnrollmentTokens_TokenHash` ON `EnrollmentTokens` (`TokenHash`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE INDEX `IX_RemoteSessions_DeviceId_OpenedAt` ON `RemoteSessions` (`DeviceId`, `OpenedAt`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE UNIQUE INDEX `IX_Roles_Name` ON `Roles` (`Name`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE INDEX `IX_UserRoles_RoleId` ON `UserRoles` (`RoleId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    CREATE UNIQUE INDEX `IX_Users_Username` ON `Users` (`Username`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260611143258_InitialSchema') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260611143258_InitialSchema', '9.0.0');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;

