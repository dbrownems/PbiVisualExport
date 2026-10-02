# PbiVisualExport

Export **all** the data behind an embedded Power BI visual to CSV, with no row limit.
Power BI's own "Export data" stops at 30k–150k rows. This module takes the visual's
fields, filters, slicers and sort and rebuilds them as one DAX query. It runs the query
over the workspace's **XMLA endpoint** and streams the rows to the browser as CSV. There
is no paging and no row cap, and memory stays flat: 2M rows × 31 columns took about 95 s
using about 50 MB.

This is a **sample and reference implementation** for exporting Power BI visuals. Adapt
it to your app, and review and test it against your own models, reports and security
requirements before you use it in production.

There are two parts: an ASP.NET Core module (this package) and a browser client. The
package embeds the client and serves it at `{prefix}/client.js`, so the client always
matches the server's version.

## Server

```csharp
builder.Services.AddPbiVisualExport(builder.Configuration.GetSection("PbiVisualExport"))
    .UseCredential(credential)                   // optional: the host's TokenCredential (else config)
    .AddAuthorizer<MyExportAuthorizer>()         // optional: who may export which report
    .AddEffectiveIdentity<MyRlsIdentity>()       // optional: run the query as the user (RLS)
    .AddAuditSink<MyAuditSink>();                // optional: one record per csv/download stream

app.UseAuthentication();
app.UseAuthorization();
app.MapPbiVisualExport("/pbi-export");           // RequireAuthorization() by default
```

```jsonc
"PbiVisualExport": {
  "AllowedWorkspaceIds": [ "<workspace-guid>" ],   // required with the default authorizer
  "Credential": { "Mode": "ServicePrincipal", "TenantId": "…", "ClientId": "…", "ClientSecret": "…" }
}
```

## Browser

Load `powerbi-client`, then `powerbi-report-authoring`, then the client:

```html
<script src="/pbi-export/client.js"></script>
<script>
  const exporter = PbiVisualExport.createExporter({ report, apiBase: "/pbi-export" });
  await exporter.addVisualMenuCommand();            // "Export all data (CSV)" in each visual's menu
  button.onclick = () => exporter.exportVisual(visual, { onProgress: p => status.textContent = p.message });
</script>
```

## Requirements

- .NET 8 or .NET 10, **x64** (the ADOMD.NET and TOM packages are x64-only).
- A workspace on Premium or Fabric capacity with **XMLA read** enabled.
- The module's identity reads model and report definitions with Fabric `getDefinition`,
  which needs **read and write** permission on both. In practice that means workspace
  **Contributor** or higher. A service principal also needs the tenant setting "Service
  principals can use Fabric APIs". `getDefinition` is blocked for semantic models with an
  encrypted sensitivity label.
- The report must be saved in **PBIR** (enhanced report format) under the default
  `Export:FieldValidation=Strict`.

See the [integration guide](https://github.com/dbrownems/PbiVisualExport#readme) for the
full configuration reference, the security model, the request contract and the known gaps.
