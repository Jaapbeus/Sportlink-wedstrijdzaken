using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SportlinkFunction
{
    public class Team
    {
        public int teamcode { get; set; }
        public int lokaleteamcode { get; set; }
        public int? poulecode { get; set; }
        public string teamnaam { get; set; }           = string.Empty;
        public string competitienaam { get; set; }     = string.Empty;
        public string klasse { get; set; }             = string.Empty;
        public string poule { get; set; }              = string.Empty;
        public string klassepoule { get; set; }        = string.Empty;
        public string spelsoort { get; set; }          = string.Empty;
        public string competitiesoort { get; set; }    = string.Empty;
        public string geslacht { get; set; }           = string.Empty;
        public string teamsoort { get; set; }          = string.Empty;
        public string leeftijdscategorie { get; set; } = string.Empty;
        public string kalespelsoort { get; set; }      = string.Empty;
        public string speeldag { get; set; }           = string.Empty;
        public string speeldagteam { get; set; }       = string.Empty;
        public string more { get; set; }               = string.Empty;


    }

    public class Match
    {
        // Shared fields (/programma + /uitslagen)
        public string wedstrijddatum { get; set; }              = string.Empty;
        public long wedstrijdcode { get; set; }
        public long wedstrijdnummer { get; set; }
        public string datum { get; set; }                       = string.Empty;
        public string wedstrijd { get; set; }                   = string.Empty;
        public string accommodatie { get; set; }                = string.Empty;
        public string aanvangstijd { get; set; }                = string.Empty;
        public string thuisteam { get; set; }                   = string.Empty;
        public string thuisteamid { get; set; }                 = string.Empty;
        public string thuisteamlogo { get; set; }               = string.Empty;
        public string thuisteamclubrelatiecode { get; set; }    = string.Empty;
        public string uitteamclubrelatiecode { get; set; }      = string.Empty;
        public string uitteam { get; set; }                     = string.Empty;
        public string uitteamid { get; set; }                   = string.Empty;
        public string uitteamlogo { get; set; }                 = string.Empty;
        public string competitiesoort { get; set; }             = string.Empty;
        public string status { get; set; }                      = string.Empty;
        public string meer { get; set; }                        = string.Empty;

        // /programma specific fields
        public string teamnaam { get; set; }                    = string.Empty;
        public int teamvolgorde { get; set; }
        public string competitie { get; set; }                  = string.Empty;
        public string klasse { get; set; }                      = string.Empty;
        public string poule { get; set; }                       = string.Empty;
        public string klassepoule { get; set; }                 = string.Empty;
        public string kaledatum { get; set; }                   = string.Empty;
        public string vertrektijd { get; set; }                 = string.Empty;
        public string verzameltijd { get; set; }                = string.Empty;
        public string scheidsrechters { get; set; }             = string.Empty;
        public string scheidsrechter { get; set; }              = string.Empty;
        public string veld { get; set; }                        = string.Empty;
        public string locatie { get; set; }                     = string.Empty;
        public string plaats { get; set; }                      = string.Empty;
        public string rijders { get; set; }                     = string.Empty;
        public string kleedkamerthuisteam { get; set; }         = string.Empty;
        public string kleedkameruitteam { get; set; }           = string.Empty;
        public string kleedkamerscheidsrechter { get; set; }    = string.Empty;

        // /uitslagen specific fields (scores + admin)
        public string datumopgemaakt { get; set; }              = string.Empty;
        public string uitslag { get; set; }                     = string.Empty;
        [JsonProperty("uitslag-regulier")]
        public string uitslag_regulier { get; set; }            = string.Empty;
        [JsonProperty("uitslag-nv")]
        public string uitslag_nv { get; set; }                  = string.Empty;
        [JsonProperty("uitslag-s")]
        public string uitslag_s { get; set; }                   = string.Empty;
        public string competitienaam { get; set; }              = string.Empty;
        public string eigenteam { get; set; }                   = string.Empty;
        public string sportomschrijving { get; set; }           = string.Empty;
        public string verenigingswedstrijd { get; set; }        = string.Empty;
    }


    public class MatchDetails
    {
        public Wedstrijdinformatie Wedstrijdinformatie { get; set; } = new Wedstrijdinformatie();
        public Officials Officials { get; set; } = new Officials();
        public Matchofficials Matchofficials { get; set; } = new Matchofficials();
        public Kleedkamers Kleedkamers { get; set; } = new Kleedkamers();
        public Accommodatie Accommodatie { get; set; } = new Accommodatie();
        public Thuisteam Thuisteam { get; set; } = new Thuisteam();
        public Uitteam Uitteam { get; set; } = new Uitteam();
    }

    public class Wedstrijdinformatie
    {
        public int Wedstrijdnummer { get; set; }
        public int Wedstijdnummerintern { get; set; }
        public string Veldnaam { get; set; }                = string.Empty;
        public string Veldlocatie { get; set; }             = string.Empty;
        public string Vertrektijd { get; set; }             = string.Empty;
        public string Rijder { get; set; }                  = string.Empty;
        public string Thuisscore { get; set; }              = string.Empty;
        public string ThuisscoreRegulier { get; set; }      = string.Empty;
        public string ThuisscoreNv { get; set; }            = string.Empty;
        public string ThuisscoreS { get; set; }             = string.Empty;
        public string Uitscore { get; set; }                = string.Empty;
        public string UitscoreRegulier { get; set; }        = string.Empty;
        public string UitscoreNv { get; set; }              = string.Empty;
        public string UitscoreS { get; set; }               = string.Empty;
        public string Klasse { get; set; }                  = string.Empty;
        public string Wedstrijdtype { get; set; }           = string.Empty;
        public string Competitietype { get; set; }          = string.Empty;
        public string Categorie { get; set; }               = string.Empty;
        public DateTime? Wedstrijddatetime { get; set; }
        public DateTime? Wedstrijddatum { get; set; }
        public string Wedstrijddatumopgemaakt { get; set; } = string.Empty;
        public string Aanvangstijd { get; set; }            = string.Empty;
        public string Aanvangstijdopgemaakt { get; set; }   = string.Empty;
        public int? Duur { get; set; }                      
        public string Speltype { get; set; }                = string.Empty;
        public string Aanduiding { get; set; }              = string.Empty;
        public string Poulecode { get; set; }               = string.Empty;
        public string Poule { get; set; }                   = string.Empty;
        public int Thuisteamid { get; set; }                
        public string Thuisteam { get; set; }               = string.Empty;
        public int Uitteamid { get; set; }                  
        public string Uitteam { get; set; }                 = string.Empty;
        public string Opmerkingen { get; set; }             = string.Empty;
    }

    public class Officials
    {
        public string Verenigingsscheidsrechtercode { get; set; }   = string.Empty;
        public string Verenigingsscheidsrechter { get; set; }       = string.Empty;
        public string Overigeofficialcode { get; set; }             = string.Empty;
        public string Overigeofficial { get; set; }                 = string.Empty;
    }

    public class Matchofficials
    {
        public string Scheidsrechters { get; set; } = string.Empty;
    }

    public class Kleedkamers
    {
        public string Thuis { get; set; }           = string.Empty;
        public string Uit { get; set; }             = string.Empty;
        public string Official { get; set; }        = string.Empty;
    }

    public class Accommodatie
    {
        public string Naam { get; set; }            = string.Empty;
        public string Straat { get; set; }          = string.Empty;
        public string Plaats { get; set; }          = string.Empty;
        public string Telefoon { get; set; }        = string.Empty;
        public string Routeplanner { get; set; }    = string.Empty;
    }

    public class Thuisteam
    {
        public string Naam { get; set; }            = string.Empty;
        public string Code { get; set; }            = string.Empty;
        public string Website { get; set; }         = string.Empty;
        public string Shirtkleur { get; set; }      = string.Empty;
        public string Straat { get; set; }          = string.Empty;
        public string Postcodeplaats { get; set; }  = string.Empty;
        public string Telefoon { get; set; }        = string.Empty;
        public string Email { get; set; }           = string.Empty;
    }

    public class Uitteam
    {
        public string Naam { get; set; }            = string.Empty;
        public string Code { get; set; }            = string.Empty;
        public string Website { get; set; }         = string.Empty;
        public string Shirtkleur { get; set; }      = string.Empty;
        public string Straat { get; set; }          = string.Empty;
        public string Postcodeplaats { get; set; }  = string.Empty;
        public string Telefoon { get; set; }        = string.Empty;
        public string Email { get; set; }           = string.Empty;
    }

}