using Microsoft.Extensions.Options;

namespace PbiVisualExport.Tests;

public class OptionsValidatorTests
{
    private static ValidateOptionsResult Validate(PbiVisualExportOptions o, bool hostCredential = false) =>
        new PbiVisualExportOptionsValidator(hostCredential
            ? new[] { new HostCredentials(new FakeCredential(), null) }
            : Array.Empty<HostCredentials>()).Validate(null, o);

    [Fact]
    public void Defaults_AreValid()
    {
        var o = new PbiVisualExportOptions();
        Assert.Equal("AzureCli", o.Credential.Mode);
        Assert.True(Validate(o).Succeeded);
    }

    [Fact]
    public void ServicePrincipal_RequiresTenantClientAndSecret()
    {
        var o = new PbiVisualExportOptions();
        o.Credential.Mode = "ServicePrincipal";
        o.Credential.ClientId = "client";
        var result = Validate(o);
        Assert.True(result.Failed);
        var failures = result.Failures!.ToList();
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.StartsWith("PbiVisualExport:Credential:TenantId is required for Credential:Mode ServicePrincipal"));
        Assert.Contains(failures, f => f.StartsWith("PbiVisualExport:Credential:ClientSecret is required"));
        Assert.Contains("PbiVisualExport__Credential__ClientSecret", result.FailureMessage);
    }

    [Fact]
    public void MasterUser_RequiresUsernameAndPassword()
    {
        var o = new PbiVisualExportOptions();
        o.Credential.Mode = "masteruser";
        o.Credential.TenantId = "t";
        o.Credential.ClientId = "c";
        var failures = Validate(o).Failures!.ToList();
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.Contains("Credential:Username"));
        Assert.Contains(failures, f => f.Contains("Credential:Password"));
    }

    [Fact]
    public void UnknownModes_Fail()
    {
        var o = new PbiVisualExportOptions();
        o.Credential.Mode = "Kerberos";
        Assert.True(Validate(o).Failed);

        o = new PbiVisualExportOptions();
        o.Credential.XmlaMode = "Interactive";
        Assert.Contains("XmlaMode 'Interactive' is not supported", Validate(o).FailureMessage);
    }

    [Fact]
    public void HostCredential_SkipsCredentialSettings()
    {
        var o = new PbiVisualExportOptions();
        o.Credential.Mode = "ServicePrincipal";
        o.Credential.XmlaMode = "bogus";
        Assert.True(Validate(o, hostCredential: true).Succeeded);
    }

    [Fact]
    public void GeneralSettings_AreAlwaysChecked()
    {
        var o = new PbiVisualExportOptions { XmlaEndpoint = " " };
        o.AllowedWorkspaceIds.Add(Guid.Empty);
        o.Export.MaxRows = -1;
        var result = Validate(o, hostCredential: true);
        Assert.Equal(new[]
        {
            "PbiVisualExport:XmlaEndpoint is required.",
            "PbiVisualExport:AllowedWorkspaceIds contains an empty GUID.",
            "PbiVisualExport:Export:MaxRows must be 0 (unlimited) or positive."
        }, result.Failures);
    }
}
