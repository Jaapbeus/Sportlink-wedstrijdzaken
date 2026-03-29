CREATE VIEW [pub].[DateTable]
	AS 
SELECT dt.[Date]
      ,dt.[Day]
      ,dt.[Month]
      ,dt.[Year]
      ,dt.[Quarter]
      ,dt.[DayOfWeek]
      ,dt.[DayName]
      ,dt.[MonthName]
      ,dt.[IsWeekend]
	  ,s.Name           As Season
  FROM [dbo].[DateTable] dt
  INNER JOIN [dbo].[Season] s
	ON dt.Date BETWEEN s.DateFrom AND s.DateUntil;
