CREATE PROCEDURE [dbo].[sp_CreateDateTable]
	@YearStart as int,
	@YearEnd   as int 
AS

IF  EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DateTable]') AND type in (N'U'))
	DROP TABLE [dbo].[DateTable]

CREATE TABLE dbo.DateTable (
    Date DATE PRIMARY KEY,
    Day INT NOT NULL,
    Month INT NOT NULL,
    Year INT NOT NULL,
    Quarter INT NOT NULL,
    DayOfWeek INT NOT NULL, -- 1 = Monday, 7 = Sunday
    DayName VARCHAR(10) NOT NULL,
    MonthName VARCHAR(15) NOT NULL,
	IsWeekend BIT NOT NULL
);

-- Populate the table with dates
WITH RecursiveDates AS (
    SELECT DATEFROMPARTS(@YearStart,1,1) AS Date -- Starting date
    UNION ALL
    SELECT DATEADD(DAY, 1, Date) 
    FROM RecursiveDates
    WHERE Date < DATEFROMPARTS(@YearEnd,12,31) -- Ending date
)

INSERT INTO dbo.DateTable (Date, Day, Month, Year, Quarter, DayOfWeek, DayName, MonthName, IsWeekend)
SELECT
    d.Date,
    DAY(d.Date) AS Day,
    MONTH(d.Date) AS Month,
    YEAR(d.Date) AS Year,
    DATEPART(QUARTER, d.Date) AS Quarter,
    DATEPART(WEEKDAY, d.Date) AS DayOfWeek,
    DATENAME(WEEKDAY, d.Date) AS DayName,
    DATENAME(MONTH, d.Date) AS MonthName,
    CASE WHEN DATEPART(WEEKDAY, d.Date) IN (6, 7) THEN 1 ELSE 0 END AS IsWeekend
FROM    RecursiveDates d
OPTION (MAXRECURSION 0); -- Allows for recursive CTE to handle larger datasets


