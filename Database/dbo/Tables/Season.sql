CREATE TABLE [dbo].[Season](
	[Name] [nchar](9) NOT NULL,
	[DateFrom] [date] NULL,
	[DateUntil] [date] NULL,
	-- #631: zonder deze constraint kon sp_UpdateSeasonTable hetzelfde seizoen bij elke deploy
	-- opnieuw invoegen. In productie stonden 3 identieke rijen voor 2026-2027, waardoor
	-- pub.DateTable (INNER JOIN op Season) 3 rijen per datum opleverde. Een regressie faalt nu
	-- hard bij de INSERT in plaats van stil data te vervuilen.
	CONSTRAINT [UQ_Season_Name] UNIQUE ([Name])
) ON [PRIMARY]
