# MONITORING.md

Observability, alerting en debugging voor de Sportlink Wedstrijdzaken applicatie.

> **Kostenstatus:** Fase A (documentatie + Application Insights) is gratis.  
> Metric Alert Rules zijn **betaald** — zie sectie [Alerting](#alerting) voor gratis alternatieven.

---

## Architectuuroverzicht

```
Azure Functions (func-[clubcode]-sportlink)
  └── Application Insights (APPLICATIONINSIGHTS_CONNECTION_STRING)
        → traces, exceptions, dependencies, customEvents

Azure Static Web Apps (swa-[clubcode]-sportlink)
  → Geen eigen Application Insights; SWA logs via Azure Monitor (gratis Activity Log)

Azure SQL ([database-naam] @ [sql-resource-group])
  → Geen Application Insights; Resource Health via Azure Portal
```

---

## Application Insights instellen

### Lokale ontwikkeling

Voeg toe aan `FunctionApp/local.settings.json`:

```json
"APPLICATIONINSIGHTS_CONNECTION_STRING": "InstrumentationKey=<key>;IngestionEndpoint=https://..."
```

Haal de connection string op via:
- Azure Portal → Application Insights resource → Overzicht → Connection String
- OF: `az monitor app-insights component show --app <naam> --resource-group <rg> --query connectionString`

### Productie (Azure)

De Function App leest `APPLICATIONINSIGHTS_CONNECTION_STRING` automatisch als App Setting.
Stel in via:

```bash
az functionapp config appsettings set \
  --name func-[clubcode]-sportlink \
  --resource-group [sql-resource-group] \
  --settings "APPLICATIONINSIGHTS_CONNECTION_STRING=<connection-string>"
```

Of via Azure Portal → Function App → Settings → Environment variables.

### host.json (sampling)

Voeg sampling toe in `FunctionApp/host.json` als de telemetrie te groot wordt (gratis tier: 5 GB/maand):

```json
{
  "version": "2.0",
  "functionTimeout": "00:10:00",
  "logging": {
    "applicationInsights": {
      "samplingSettings": {
        "isEnabled": true,
        "maxTelemetryItemsPerSecond": 5
      }
    }
  }
}
```

---

## Alerting

### Gratis alert-typen

| Type | Kosten | Wanneer gebruiken |
|---|---|---|
| **Activity Log Alerts** | Gratis | Deploy-fout, resource-verwijdering, config-wijziging |
| **Resource Health Alerts** | Gratis | Function App of SQL onbereikbaar |

### Betaalde alert-typen (expliciete goedkeuring vereist)

| Type | Kosten | Status |
|---|---|---|
| Metric Alert Rules | Betaald per time series | ⚠️ **Niet geïmplementeerd** — vereist goedkeuring eigenaar |

Bron: [Azure Monitor cost — alerts](https://learn.microsoft.com/azure/azure-monitor/fundamentals/best-practices-cost#alerts)

### Alert-drempelwaarden (toekomstige implementatie via Bicep)

| Metriek | Warning | Critical |
|---|---|---|
| HTTP 5xx error rate | > 1% / 5 min | > 5% / 2 min |
| HTTP 4xx error rate | > 5% / 5 min | > 15% / 2 min |
| p95 response time | > 1 s | > 2 s |
| Health endpoint DOWN | — | 3 achtereenvolgende fouten |

### Activity Log Alert aanmaken (gratis)

Alert bij deploy-fout (Function App restart mislukt):

```bash
az monitor activity-log alert create \
  --name "FunctionApp-deploy-fout" \
  --resource-group [sql-resource-group] \
  --scopes "/subscriptions/<id>/resourceGroups/[sql-resource-group]/providers/Microsoft.Web/sites/func-[clubcode]-sportlink" \
  --condition category=Administrative and operationName=Microsoft.Web/sites/write and status=Failed \
  --action-group /subscriptions/<id>/resourceGroups/<rg>/providers/microsoft.insights/actionGroups/<naam>
```

### Resource Health Alert aanmaken (gratis)

```bash
az monitor activity-log alert create \
  --name "FunctionApp-resource-health" \
  --resource-group [sql-resource-group] \
  --scopes "/subscriptions/<id>/resourceGroups/[sql-resource-group]" \
  --condition category=ResourceHealth and resourceType=Microsoft.Web/sites \
  --action-group /subscriptions/<id>/resourceGroups/<rg>/providers/microsoft.insights/actionGroups/<naam>
```

---

## KQL-queries voor debugging

Gebruik deze queries in Azure Portal → Application Insights → Logs.

### Request tracing via Correlation-ID

Elke admin-response bevat `x-correlation-id` in de response header. Gebruik dat ID om de volledige request-keten te reconstrueren:

```kql
traces
| where customDimensions.CorrelationId == "<id>"
| order by timestamp asc
| project timestamp, message, severityLevel, operation_Name
```

### Fouten in de afgelopen 24 uur

```kql
exceptions
| where timestamp > ago(24h)
| order by timestamp desc
| project timestamp, type, outerMessage, operation_Name, customDimensions.CorrelationId
```

### Trage requests (> 1 seconde)

```kql
requests
| where duration > 1000
| where timestamp > ago(1h)
| order by duration desc
| project timestamp, name, duration, resultCode, url
```

### Email-verwerking overzicht

```kql
traces
| where operation_Name contains "Email"
| where timestamp > ago(7d)
| order by timestamp desc
| project timestamp, message, severityLevel
```

### Dagelijkse sync monitoring

```kql
traces
| where operation_Name in ("FetchAndStoreApiData", "SyncAndStoreViaHttpTrigger")
| where timestamp > ago(7d)
| summarize runs=count(), fouten=countif(severityLevel >= 2) by bin(timestamp, 1d)
| order by timestamp desc
```

### 500-fouten per endpoint

```kql
requests
| where resultCode startswith "5"
| where timestamp > ago(7d)
| summarize count() by name, resultCode
| order by count_ desc
```

---

## Escalatiematrix

| Prioriteit | Conditie | Actie | Reactietijd |
|---|---|---|---|
| P1 — Kritiek | Health endpoint DOWN na 3 pogingen | Directe notificatie eigenaar + CISO | < 30 min |
| P1 — Kritiek | Security Gate CI rood na push naar main | STOP — geen merge, directe fix | Onmiddellijk |
| P2 — Hoog | > 5% HTTP 5xx in 2 min | Onderzoek Azure Portal + App Insights | < 2 uur |
| P2 — Hoog | Deploy-fout in GitHub Actions | Bekijk workflow-log, hotfix aanmaken | < 4 uur |
| P3 — Normaal | Dagelijkse sync mislukt | Controleer App Insights, handmatig herstarten | Volgende werkdag |
| P3 — Normaal | Email-verwerking gestopt | Controleer EmailProcessor logs | Volgende werkdag |

---

## GitHub Actions monitoring

### CI-status bekijken

```bash
gh run list --branch main --limit 10
gh run view <run-id>
```

### Deploy-pipeline controleren

```bash
gh run list --workflow deploy.yml --limit 5
```

### PR checks bewaken

```bash
gh pr checks <pr-nr>
```

---

## Bekende beperkingen

| Beperking | Oorzaak | Workaround |
|---|---|---|
| Application Insights niet geconfigureerd | `APPLICATIONINSIGHTS_CONNECTION_STRING` niet ingesteld in Azure | Zie sectie [Application Insights instellen](#application-insights-instellen) |
| Geen Metric Alerts | Betaald; expliciete goedkeuring vereist | Activity Log Alerts als gratis alternatief |
| Blazor WASM heeft geen eigen telemetrie | SWA bevat geen Application Insights SDK | Fouten zichtbaar via browser F12 / SWA-logs |
| SQL monitoring beperkt | Free tier SQL heeft geen query-telemetrie | Gebruik `sys.dm_exec_query_stats` voor lokale diagnose |
