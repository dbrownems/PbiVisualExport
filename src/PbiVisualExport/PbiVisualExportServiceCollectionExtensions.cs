using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PbiVisualExport.Auth;
using PbiVisualExport.Services;

namespace PbiVisualExport;

public static class PbiVisualExportServiceCollectionExtensions
{
    /// <summary>
    /// Registers the visual export module. Bind it to its configuration section and map the
    /// endpoints with <see cref="PbiVisualExportEndpointRouteBuilderExtensions.MapPbiVisualExport"/>:
    /// <code>
    /// builder.Services.AddPbiVisualExport(builder.Configuration.GetSection("PbiVisualExport"));
    /// ...
    /// app.MapPbiVisualExport("/pbi-export");
    /// </code>
    /// </summary>
    /// <param name="section">The module's configuration section (conventionally <c>PbiVisualExport</c>).</param>
    /// <param name="configure">Optional code configuration, applied after the section is bound.</param>
    public static PbiVisualExportBuilder AddPbiVisualExport(this IServiceCollection services, IConfiguration section,
        Action<PbiVisualExportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        var options = services.AddOptions<PbiVisualExportOptions>().Bind(section);
        if (configure is not null) options.Configure(configure);
        return AddCore(services, options);
    }

    /// <summary>Registers the module configured in code only (no configuration section).</summary>
    public static PbiVisualExportBuilder AddPbiVisualExport(this IServiceCollection services, Action<PbiVisualExportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddCore(services, services.AddOptions<PbiVisualExportOptions>().Configure(configure));
    }

    private static PbiVisualExportBuilder AddCore(IServiceCollection services, OptionsBuilder<PbiVisualExportOptions> options)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PbiVisualExportOptions>, PbiVisualExportOptionsValidator>());
        options.ValidateOnStart();

        services.AddAuthorization();
        services.AddHttpClient();
        services.AddMemoryCache();

        services.TryAddSingleton(sp => new PbiTokenProvider(
            sp.GetRequiredService<IOptions<PbiVisualExportOptions>>(),
            sp.GetService<HostCredentials>(),
            sp.GetRequiredService<ILogger<PbiTokenProvider>>()));
        services.TryAddSingleton(sp => new XmlaTokenProvider(
            sp.GetRequiredService<PbiTokenProvider>(),
            sp.GetRequiredService<IOptions<PbiVisualExportOptions>>(),
            sp.GetService<HostCredentials>(),
            sp.GetRequiredService<ILogger<XmlaTokenProvider>>()));
        services.TryAddSingleton<XmlaConnectionFactory>();
        services.TryAddSingleton<IPowerBiCatalog, PowerBiCatalog>();
        services.TryAddSingleton<MetadataCache>();
        services.TryAddSingleton<FabricDefinitionClient>();
        services.TryAddSingleton<SemanticModelMetadataService>();
        services.TryAddSingleton<ReportDefinitionService>();
        services.TryAddSingleton<DaxQueryBuilder>();
        services.TryAddSingleton<ExportConcurrencyLimiter>();
        services.TryAddSingleton<IVisualExportAuthorizer, AllowListAuthorizer>();
        services.TryAddScoped<ExportRequestResolver>();
        services.TryAddScoped<CsvExportService>();
        services.TryAddScoped<ExportHandler>();
        return new PbiVisualExportBuilder(services);
    }
}

/// <summary>Customizes the module registered by <c>AddPbiVisualExport</c>.</summary>
public sealed class PbiVisualExportBuilder
{
    internal PbiVisualExportBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>
    /// Uses the host's credential (e.g. a <c>ManagedIdentityCredential</c>, or the service principal
    /// the host already uses for embedding) instead of <see cref="PbiVisualExportOptions.Credential"/>.
    /// It must mint tokens for the Power BI and Fabric resources and have access to the workspaces.
    /// </summary>
    /// <param name="xmla">Credential for the XMLA endpoint; null = <paramref name="credential"/>.</param>
    public PbiVisualExportBuilder UseCredential(TokenCredential credential, TokenCredential? xmla = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Services.RemoveAll<HostCredentials>();
        Services.AddSingleton(new HostCredentials(credential, xmla));
        return this;
    }

    /// <summary>Replaces the default authorizer (the workspace allow-list alone).</summary>
    public PbiVisualExportBuilder AddAuthorizer<T>() where T : class, IVisualExportAuthorizer
    {
        Services.Replace(ServiceDescriptor.Scoped<IVisualExportAuthorizer, T>());
        return this;
    }

    /// <summary>Runs export queries as a per-user <see cref="EffectiveIdentity"/> (row-level security).</summary>
    public PbiVisualExportBuilder AddEffectiveIdentity<T>() where T : class, IEffectiveIdentityProvider
    {
        Services.Replace(ServiceDescriptor.Scoped<IEffectiveIdentityProvider, T>());
        return this;
    }

    /// <summary>Adds an audit sink; any number may be added.</summary>
    public PbiVisualExportBuilder AddAuditSink<T>() where T : class, IExportAuditSink
    {
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IExportAuditSink, T>());
        return this;
    }
}

internal sealed class PbiVisualExportOptionsValidator : IValidateOptions<PbiVisualExportOptions>
{
    private readonly bool _hostCredential;

    public PbiVisualExportOptionsValidator(IEnumerable<HostCredentials> host) => _hostCredential = host.Any();

    public ValidateOptionsResult Validate(string? name, PbiVisualExportOptions o)
    {
        var s = PbiVisualExportOptions.DefaultSectionName;
        var problems = new List<string>();
        if (!_hostCredential)
        {
            problems.AddRange(PbiTokenProvider.Validate(o.Credential));
            try { XmlaTokenProvider.ResolveMode(o.Credential); }
            catch (InvalidOperationException ex) { problems.Add(ex.Message); }
        }
        if (string.IsNullOrWhiteSpace(o.XmlaEndpoint)) problems.Add($"{s}:XmlaEndpoint is required.");
        if (o.AllowedWorkspaceIds.Contains(Guid.Empty)) problems.Add($"{s}:AllowedWorkspaceIds contains an empty GUID.");
        if (o.Export.MaxRows < 0) problems.Add($"{s}:Export:MaxRows must be 0 (unlimited) or positive.");
        return problems.Count > 0 ? ValidateOptionsResult.Fail(problems) : ValidateOptionsResult.Success;
    }
}
