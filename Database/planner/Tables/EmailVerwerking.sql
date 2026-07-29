CREATE TABLE [planner].[EmailVerwerking] (
    [Id]                    INT             IDENTITY(1,1) NOT NULL,
    [MessageId]             NVARCHAR(500)   NOT NULL,
    [ConversationId]        NVARCHAR(500)   NULL,
    [Afzender]              NVARCHAR(200)   NOT NULL,
    [Onderwerp]             NVARCHAR(500)   NOT NULL,
    [OntvangstDatum]        DATETIME2       NOT NULL,
    [EmailBody]             NVARCHAR(MAX)   NULL,
    [VerzoekType]           NVARCHAR(50)    NOT NULL,
    [GeextraheerdeData]     NVARCHAR(MAX)   NULL,
    [PlannerResponse]       NVARCHAR(MAX)   NULL,
    [AntwoordEmail]         NVARCHAR(MAX)   NULL,
    -- #765: kan een door de beheerder ingetypte lijst van tot 15 ontvangers bevatten
    -- ("naam" <adres>; ...), niet langer alleen het ene server-side opgezochte coach-adres.
    [VerstuurdNaar]         NVARCHAR(1000)  NULL,
    -- "Wij hebben op dit bericht geantwoord", losgekoppeld van het adres in VerstuurdNaar (#718).
    -- Die kolom is een persoonsgegeven en wordt na 30 dagen geanonimiseerd; het FEIT dat er
    -- geantwoord is, is dat niet. Beide betekenissen zaten op één kolom, waardoor de anonimisering
    -- stilzwijgend twee dingen sloopte: de replydetectie (en daarmee het zelflerende deel) en de
    -- harde grens tegen een tweede antwoord. Deze kolom overleeft de anonimisering bewust.
    [IsBeantwoord]          BIT             NOT NULL CONSTRAINT [DF_EmailVerwerking_IsBeantwoord] DEFAULT 0,
    -- Verzendintentie: gezet vlak VÓÓR de verzendpoging, gewist zodra die aantoonbaar mislukt (#716).
    -- Een gevulde waarde bij IsBeantwoord = 0 betekent dus: er is verstuurd of misschien verstuurd,
    -- en we weten de uitkomst niet — dan mag er nooit blind opnieuw verstuurd worden.
    [VerzendPogingOpUtc]    DATETIME2       NULL,
    [Status]                NVARCHAR(30)    NOT NULL CONSTRAINT [DF_EmailVerwerking_Status] DEFAULT 'Ontvangen',
    [FoutMelding]           NVARCHAR(1000)  NULL,
    [IsReplyOpOnsAntwoord]  BIT             NULL,
    [ReplyOpVerwerkingId]   INT             NULL,
    -- Aantal verwerkingspogingen. Voorkomt dat een structureel falend bericht elke poll opnieuw
    -- wordt geprobeerd en zo de wachtrij van 10 oudste ongelezen berichten blokkeert (#712).
    [Pogingen]              INT             NOT NULL CONSTRAINT [DF_EmailVerwerking_Pogingen] DEFAULT 0,
    [ClubCode]              NVARCHAR(20)    NOT NULL CONSTRAINT [CK_EmailVerwerking_ClubCode] CHECK (LEN([ClubCode]) > 0),
    [mta_inserted]          DATETIME        NOT NULL CONSTRAINT [DF_EmailVerwerking_Ins] DEFAULT GETUTCDATE(),
    [mta_modified]          DATETIME        NOT NULL CONSTRAINT [DF_EmailVerwerking_Mod] DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_EmailVerwerking] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [UQ_EmailVerwerking_MessageId] UNIQUE ([MessageId])
);
