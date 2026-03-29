using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction
{
    //public static class CreateStagingTableMatchDetails
    public static class CreateStagingTable
    {
        public static async Task ExecuteAsync(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentException("Table name cannot be null or empty.", nameof(tableName));
            }
            using (SqlConnection connection = new SqlConnection(DatabaseConfig.ConnectionString))
            {
                string query = string.Empty;

                switch (tableName.ToLower())
                {
                    case "teams":
                        query = @"
                        IF  EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[stg].[teams]') AND type in (N'U'))
	                        DROP TABLE [stg].[teams];

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
                        ) ON [PRIMARY] ;";
                        break;

                    case "matches":
                        query = @"
                        IF  EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[stg].[matches]') AND type in (N'U'))
	                        DROP TABLE [stg].[matches];

                        CREATE TABLE [stg].[matches](
                            -- Shared fields (/programma + /uitslagen)
                            [wedstrijddatum]            NVARCHAR(50)    NULL,
                            [wedstrijdcode]             BIGINT          NULL,
                            [wedstrijdnummer]           BIGINT          NULL,
                            [datum]                     NVARCHAR(50)    NULL,
                            [wedstrijd]                 NVARCHAR(200)   NULL,
                            [accommodatie]              NVARCHAR(200)   NULL,
                            [aanvangstijd]              NVARCHAR(50)    NULL,
                            [thuisteam]                 NVARCHAR(100)   NULL,
                            [thuisteamid]               NVARCHAR(50)    NULL,
                            [thuisteamlogo]             NVARCHAR(1000)  NULL,
                            [thuisteamclubrelatiecode]  NVARCHAR(50)    NULL,
                            [uitteamclubrelatiecode]    NVARCHAR(50)    NULL,
                            [uitteam]                   NVARCHAR(100)   NULL,
                            [uitteamid]                 NVARCHAR(50)    NULL,
                            [uitteamlogo]               NVARCHAR(1000)  NULL,
                            [competitiesoort]           NVARCHAR(200)   NULL,
                            [status]                    NVARCHAR(50)    NULL,
                            [meer]                      NVARCHAR(1000)  NULL,
                            -- /programma specific fields
                            [teamnaam]                  NVARCHAR(100)   NULL,
                            [teamvolgorde]              INT             NULL,
                            [competitie]                NVARCHAR(200)   NULL,
                            [klasse]                    NVARCHAR(100)   NULL,
                            [poule]                     NVARCHAR(100)   NULL,
                            [klassepoule]               NVARCHAR(200)   NULL,
                            [kaledatum]                 NVARCHAR(50)    NULL,
                            [vertrektijd]               NVARCHAR(50)    NULL,
                            [verzameltijd]              NVARCHAR(50)    NULL,
                            [scheidsrechters]           NVARCHAR(500)   NULL,
                            [scheidsrechter]            NVARCHAR(200)   NULL,
                            [veld]                      NVARCHAR(100)   NULL,
                            [locatie]                   NVARCHAR(100)   NULL,
                            [plaats]                    NVARCHAR(100)   NULL,
                            [rijders]                   NVARCHAR(200)   NULL,
                            [kleedkamerthuisteam]       NVARCHAR(50)    NULL,
                            [kleedkameruitteam]         NVARCHAR(50)    NULL,
                            [kleedkamerscheidsrechter]  NVARCHAR(50)    NULL,
                            -- /uitslagen specific fields (scores + admin fields)
                            [datumopgemaakt]            NVARCHAR(50)    NULL,
                            [uitslag]                   NVARCHAR(50)    NULL,
                            [uitslag-regulier]          NVARCHAR(50)    NULL,
                            [uitslag-nv]                NVARCHAR(50)    NULL,
                            [uitslag-s]                 NVARCHAR(50)    NULL,
                            [competitienaam]            NVARCHAR(200)   NULL,
                            [eigenteam]                 NVARCHAR(50)    NULL,
                            [sportomschrijving]         NVARCHAR(100)   NULL,
                            [verenigingswedstrijd]      NVARCHAR(50)    NULL
                        ) ON [PRIMARY];";
                        break;

                    case "matchdetails":
                        query = @"
                        IF  EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[stg].[matchdetails]') AND type in (N'U'))
                            DROP TABLE [stg].[matchdetails];

                        CREATE TABLE [stg].[matchdetails] (
                            WedstrijdCode INT PRIMARY KEY,
                            InternCode INT,
                            VeldNaam NVARCHAR(200),
                            VeldLocatie NVARCHAR(200),
                            VertrekTijd NVARCHAR(200),
                            Rijder NVARCHAR(200),
                            ThuisScore NVARCHAR(20),
                            ThuisScoreRegulier NVARCHAR(20),
                            ThuisScoreNV NVARCHAR(20),
                            ThuisScoreS NVARCHAR(20),
                            UitScore NVARCHAR(20),
                            UitScoreRegulier NVARCHAR(20),
                            UitScoreNV NVARCHAR(20),
                            UitScoreS NVARCHAR(20),
                            Klasse NVARCHAR(200),
                            WedstrijdType NVARCHAR(200),
                            CompetitieType NVARCHAR(200),
                            Categorie NVARCHAR(20),
                            MatchDateTime DATETIME,
                            MatchDate DATE,
                            Aanvangstijd TIME,
                            Duration INT,
                            SpelType NVARCHAR(200),
                            Aanduiding NVARCHAR(200),
                            PouleCode INT,
                            Poule NVARCHAR(200),
                            ThuisTeamID INT,
                            ThuisTeam NVARCHAR(200),
                            UitTeamID INT,
                            UitTeam NVARCHAR(200),
                            Opmerkingen NVARCHAR(MAX),
                            VerenigingScheidsrechterCode NVARCHAR(200),
                            VerenigingScheidsrechter NVARCHAR(200),
                            OverigeOfficialCode NVARCHAR(200),
                            OverigeOfficial NVARCHAR(200), 
                            Scheidsrechters NVARCHAR(1000), 
                            KleedkamerThuis NVARCHAR(200), 
                            KleedkamerUit NVARCHAR(200), 
                            KleedkamerOfficial NVARCHAR(200), 
                            AccommodatieNaam NVARCHAR(200), 
                            AccommodatieStraat NVARCHAR(150), 
                            AccommodatiePlaats NVARCHAR(150), 
                            AccommodatieTelefoon NVARCHAR(200), 
                            AccommodatieRouteplanner NVARCHAR(1000), 
                            ThuisTeamNaam NVARCHAR(200), 
                            ThuisTeamCode NVARCHAR(200), 
                            ThuisTeamWebsite NVARCHAR(1000), 
                            ThuisTeamShirtKleur NVARCHAR(200), 
                            ThuisTeamStraat NVARCHAR(150), 
                            ThuisTeamPostcodePlaats NVARCHAR(150), 
                            ThuisTeamTelefoon NVARCHAR(200), 
                            ThuisTeamEmail NVARCHAR(200), 
                            UitTeamNaam NVARCHAR(200), 
                            UitTeamCode NVARCHAR(200), 
                            UitTeamWebsite NVARCHAR(1000), 
                            UitTeamShirtKleur NVARCHAR(200), 
                            UitTeamStraat NVARCHAR(150), 
                            UitTeamPostcodePlaats NVARCHAR(150), 
                            UitTeamTelefoon NVARCHAR(200), 
                            UitTeamEmail NVARCHAR(200) );
                        ";
                        break;
                    default:
                        throw new ArgumentException("Invalid table name.", nameof(tableName));
                }

                await connection.OpenAsync();
                using (SqlCommand cmd = new SqlCommand(query, connection))
                {
                    await cmd.ExecuteNonQueryAsync();
                }
                connection.Close(); 
            }
        }
    }
}
