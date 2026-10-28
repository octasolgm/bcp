namespace Reguliq.Api.Services.NewDashboard.Demo;

public sealed class NdDemoIsolationOptions
{
    /// <summary>When false, all demo isolation and AI interception is bypassed.</summary>
    public bool DemoModeEnabled { get; set; } = true;

    /// <summary>Production internal doc to clone parse/sections for demo uploads (AML Manual 290626).</summary>
    public Guid DemoInternalTemplateDocumentId { get; set; } =
        Guid.Parse("16d820d9-3870-4497-bcb2-e46f7261f348");

    /// <summary>Production regulation doc to clone parse/points for demo uploads (CBUAE_EN_3945_VER2).</summary>
    public Guid DemoRegulationTemplateDocumentId { get; set; } =
        Guid.Parse("5836bf2a-e1f9-4a65-8ae0-8fee71f7cef6");

    /// <summary>Production regulation doc to clone for demo TFS Guidelines uploads (~96 points).</summary>
    public Guid DemoTfsRegulationTemplateDocumentId { get; set; } =
        Guid.Parse("7555012e-b941-43c6-8502-f9687da8b6ba");

    /// <summary>Known file hash for I M P T F S.pdf — used to locate internal template when ID is unset.</summary>
    public string DemoImptfsInternalFileHash { get; set; } =
        "6a0a0bd13c7a32ea10c43c9a8391347a7e0caceaa0b17dd6443e9ee622111717";
}
