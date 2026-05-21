using Chiron.Cds.Engine.Primitives;
using Chiron.Cds.Engine.Rules.Renal;
using Chiron.Cds.Engine.Tests.Fixtures;

namespace Chiron.Cds.Engine.Tests;

/// <summary>
/// One-shot helper: prints the canonical fingerprint each fixture produces.
/// Used to bake the expected fingerprints into the JSON fixtures themselves
/// after engine logic changes. Marked Skip in CI; run locally with:
///   dotnet test --filter FullyQualifiedName~FingerprintCapture -- xunit.execution.runExplicit=true
/// </summary>
public class FingerprintCapture
{
    [Fact(Skip = "manual fingerprint regeneration; remove Skip to print")]
    public void Print_All_Fixture_Fingerprints()
    {
        foreach (var file in FixtureLoader.AllFixtureFiles())
        {
            var fixture = FixtureLoader.Load(file);
            if (fixture.ExpectAlert is null) continue;

            var engine = new Engine().RegisterPack(typeof(MetforminRenalRule).Assembly);
            var result = engine.Evaluate(
                fixture.ToEnginePatient(),
                fixture.ToEngineMedications(),
                fixture.ToEngineLabs(),
                fixture.ToEngineConditions());

            var alert = result.Alerts.SingleOrDefault(a => a.RuleId == fixture.ExpectAlert.RuleId);
            Console.WriteLine($"{file}: {alert?.Fingerprint ?? "<none>"}");
        }
    }
}
