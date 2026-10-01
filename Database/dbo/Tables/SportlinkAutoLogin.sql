CREATE TABLE [dbo].[SportlinkAutoLogin] (
    [ClubCode]             NVARCHAR(20) NOT NULL,
    [RolNaam]              NVARCHAR(50) NOT NULL,
    [CredentialsEncrypted] NVARCHAR(MAX) NULL,
    [RefreshEncrypted]     NVARCHAR(MAX) NULL,
    [LastLoginUtc]         DATETIME2(7) NULL,
    [RetryAfterUtc]        DATETIME2(7) NULL,
    [LastError]            NVARCHAR(64) NULL,
    [FailureCount]         INT NOT NULL CONSTRAINT [DF_SportlinkAutoLogin_FailureCount] DEFAULT (0),
    CONSTRAINT [PK_SportlinkAutoLogin] PRIMARY KEY CLUSTERED ([ClubCode] ASC, [RolNaam] ASC)
);
