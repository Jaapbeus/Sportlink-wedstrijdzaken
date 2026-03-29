-- This table will be DROPPED and CREATED every time the functionApp process is runs.
CREATE TABLE [stg].[teams](
	[teamcode]				[bigint]            NULL,
	[lokaleteamcode]		[bigint]            NULL,
	[poulecode]				[bigint]            NULL,
	[teamnaam]				[nvarchar](100)     NULL,
	[competitienaam]		[nvarchar](200)     NULL,
	[klasse]				[nvarchar](200)     NULL,
	[poule]					[nvarchar](50)      NULL,
	[klassepoule]			[nvarchar](200)     NULL,
	[spelsoort]				[nvarchar](50)      NULL,
	[competitiesoort]		[nvarchar](50)      NULL,
	[geslacht]				[nvarchar](50)      NULL,
	[teamsoort]				[nvarchar](50)      NULL,
	[leeftijdscategorie]	[nvarchar](50)      NULL,
	[kalespelsoort]			[nvarchar](50)      NULL,
	[speeldag]				[nvarchar](50)      NULL,
	[speeldagteam]			[nvarchar](100)     NULL,
	[more]					[nvarchar](200)     NULL
) ON [PRIMARY] ;