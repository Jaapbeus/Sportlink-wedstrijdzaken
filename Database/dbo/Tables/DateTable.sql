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