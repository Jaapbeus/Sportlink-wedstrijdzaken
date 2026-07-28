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

We filteren zodat alleen begeleiders zichtbaar zijn en geen spelers.

1. Ga naar **Personen**

2. Kies **Teams**

3. Klik bij **Bondsteam** op **alles selecteren**

   > Deze stap wordt gemakkelijk overgeslagen. Zonder deze selectie komen ook lokale (niet-bonds)teams
   > in de export terecht, waardoor de lijst ruis bevat.

4. Klik bij **Rol binnen het team** op **alles selecteren** — nu staan alle rollen aangevinkt

5. Verwijder het vinkje bij de volgende vier rollen door er één voor één op te klikken:

   - **Teamspeler / Aanvaller**
   - **Teamspeler / Keeper**
   - **Teamspeler / Middenvelder**
   - **Teamspeler / Verdediger**

   > Controleer dat deze vier rollen **niet** aangevinkt zijn. Alle andere rollen zoals trainer, leider en coach mogen aangevinkt blijven.

6. Klik op de knop **Zoeken**

7. Wacht even — dit duurt soms 5 tot 10 seconden. Je ziet daarna het resultaat verschijnen met het aantal gevonden personen.

---

## Stap 3 — De lijst exporteren

1. Kijk in de grijze balk direct boven de lijst met resultaten. Aan de rechterkant van die balk staan een paar kleine icoontjes.

2. Klik op het **exporteer-icoontje** — dit ziet eruit als een tabel met een pijl naar beneden.

3. Er verschijnt een klein venster. Klik daarin op **Download**.

4. Het bestand wordt nu gedownload naar je **Downloadmap**.

---

## Stap 4 — Bestand importeren

Er zijn twee manieren om het bestand te importeren. **Optie A is de eenvoudigste** en vereist geen
technische kennis.

> **Let op — geldt voor beide opties:** een import **vervangt de bestaande teambegeleiding van de club
> volledig**. Alle eerder geïmporteerde rijen worden eerst verwijderd, daarna wordt de nieuwe lijst
> ingelezen. Er wordt niets samengevoegd. Is je export onvolledig (bijv. Bondsteam niet geselecteerd in
> stap 2), importeer dan simpelweg een nieuwe, complete export — die overschrijft de foutieve lijst weer.

### Optie A — via de Admin GUI (aanbevolen)

1. Open de Admin GUI en ga naar **Teambegeleiding**
2. Scroll naar **Teambegeleiding importeren**
3. Kies het gedownloade bestand
4. Controleer de voorbeeldweergave en bevestig de import

De CSV wordt in de browser ingelezen en verwerkt — er wordt geen bestand op de server opgeslagen.

### Optie B — via het PowerShell-script

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
| Veel minder personen gevonden dan verwacht | Filter niet goed ingesteld | Herhaal stap 2 en controleer of de 4 Teamspeler-rollen uitgevinkt zijn |
| Lijst bevat teams die geen bondsteam zijn | **Bondsteam** niet op *alles selecteren* gezet (stap 2.3) | Herhaal stap 2 mét die selectie en importeer opnieuw — de nieuwe import vervangt de foutieve lijst volledig |
| Geen exportknop zichtbaar | Onvoldoende rechten | Vraag een beheerder om de export uit te voeren |
| Script geeft een fout | Geen bestand gevonden | Controleer of de download in stap 3 geslaagd is en het bestand in de Downloadmap staat |
| Verificatiecode werkt niet | Code verlopen | Wacht tot de authenticator-app een nieuwe code toont en probeer opnieuw |
| Waarschuwing "exacte duplicaat-rij(en) overgeslagen" | Sportlink-export bevat dezelfde persoon met exact dezelfde rol twee keer | Geen actie nodig — de import slaat deze duplicaten automatisch over, de rest van de lijst is correct geïmporteerd |
