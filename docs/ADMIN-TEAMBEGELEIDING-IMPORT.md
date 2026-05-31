# Handleiding: Teambegeleiding export uit Sportlink Club

Deze handleiding legt stap voor stap uit hoe je de lijst met teambegeleiders (trainers, leiders, coaches) exporteert uit Sportlink Club. Je hebt hier geen technische kennis voor nodig.

---

## Wat heb je nodig?

- Toegang tot [club.sportlink.com](https://club.sportlink.com) met een beheerdersaccount
- Je gebruikersnaam en wachtwoord voor Sportlink

---

## Stap 1 — Inloggen op Sportlink Club

1. Ga naar [https://club.sportlink.com/member/search](https://club.sportlink.com/member/search)
2. Je wordt doorgestuurd naar de loginpagina
3. Vul je **e-mailadres** en **wachtwoord** in
4. Klik op **Inloggen**
5. Als er een verificatiecode gevraagd wordt, vul deze dan in (je ontvangt die via de authenticator-app)

> Als je al ingelogd bent, ga je direct naar stap 2.

---

## Stap 2 — Filter instellen: alleen teambegeleiding

Je ziet nu de ledenzoekopdracht. We filteren zodat alleen begeleiders zichtbaar zijn en geen spelers.

1. Klik op de knop **Teams** in de filterbalk bovenaan de pagina

2. Klik in het uitklapmenu op **Rol binnen team**

3. Je ziet een lijst met rollen en aangevinkte checkboxes. Verwijder het vinkje bij de volgende vier rollen door er één voor één op te klikken:

   - **Teamspeler / Aanvaller**
   - **Teamspeler / Keeper**
   - **Teamspeler / Middenvelder**
   - **Teamspeler / Verdediger**

   > Controleer dat deze vier rollen **niet** aangevinkt zijn. Alle andere rollen zoals trainer, leider en coach mogen aangevinkt blijven.

4. Klik op de knop **Zoek**

5. Wacht even — dit duurt soms 5 tot 10 seconden. Je ziet daarna het resultaat verschijnen met het aantal gevonden personen.

---

## Stap 3 — De lijst exporteren

1. Kijk in de grijze balk direct boven de lijst met resultaten. Aan de rechterkant van die balk staan een paar kleine icoontjes.

2. Klik op het **exporteer-icoontje** — dit ziet eruit als een tabel met een pijl naar beneden.

3. Er verschijnt een klein venster. Klik daarin op **Download**.

4. Het bestand wordt nu gedownload naar je **Downloadmap**.

---

## Stap 4 — Bestand importeren naar de database

Nu het bestand gedownload is, voer je het importscript uit zodat de gegevens in de database worden bijgewerkt.

1. Open **PowerShell** (zoek via het Startmenu op "PowerShell")

2. Navigeer naar de projectmap (vervang `<PROJECTMAP>` door het pad naar jouw lokale repo):

   ```powershell
   cd <PROJECTMAP>
   ```

3. Voer het importscript uit:

   ```powershell
   .\exports\import-teambegeleiding-to-sql.ps1
   ```

4. Het script importeert het bestand automatisch en laat aan het einde zien hoeveel personen verwerkt zijn.

> **Let op:** het bestand bevat persoonsgegevens van clubleden. Na de import kun je de CSV verwijderen door het script uit te voeren met `-DeleteCsvAfterImport $true`.

---

## Controleren of het gelukt is

Na het uitvoeren van het script zie je een samenvatting zoals:

```
Klaar!
  Geïmporteerd : [aantal] personen
  Tabel        : avg.Teambegeleiding
  Duur         : [duur] ms
  Datum        : [datum]
```

Klopt het aantal met wat je in Sportlink Club hebt gezien? Dan is alles goed gegaan.

---

## Hoe vaak moet dit?

Deze export wordt **wekelijks** uitgevoerd — kies een vast moment dat past bij jouw club. Zo is de lijst altijd actueel met nieuwe leden, gewijzigde rollen en vertrokken begeleiders.

---

## Problemen?

| Probleem | Mogelijke oorzaak | Oplossing |
|---|---|---|
| Minder dan 400 personen gevonden | Filter niet goed ingesteld | Herhaal stap 2 en controleer of de 4 Teamspeler-rollen uitgevinkt zijn |
| Geen exportknop zichtbaar | Onvoldoende rechten | Vraag een beheerder om de export uit te voeren |
| Script geeft een fout | Geen bestand gevonden | Controleer of de download in stap 3 geslaagd is en het bestand in de Downloadmap staat |
| Verificatiecode werkt niet | Code verlopen | Wacht tot de authenticator-app een nieuwe code toont en probeer opnieuw |
