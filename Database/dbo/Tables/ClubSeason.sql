CREATE TABLE [dbo].[ClubSeason](
	-- #1715: de seizoenen per club, elk uit de eigen SeasonStartMonth (dbo.AppSettings). Geen waarde uit de
	-- rijen van alle clubs afleiden (geen MIN()/MAX() over clubs). Vervangt het clubneutrale dbo.Season,
	-- dat voor de overgang (expand/contract) blijft staan maar door de code niet meer gelezen wordt.
	[ClubCode] [nvarchar](20) NOT NULL,
	[Name] [nchar](9) NOT NULL,
	[DateFrom] [date] NOT NULL,
	[DateUntil] [date] NOT NULL,
	CONSTRAINT [PK_ClubSeason] PRIMARY KEY ([ClubCode], [Name])
) ON [PRIMARY]
