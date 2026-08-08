using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Fixture.Analyzers;

[Generator(LanguageNames.CSharp)]
public sealed class FixtureGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static output =>
        {
            System.Threading.Thread.SpinWait(20_000);
            output.AddSource(
                "YaapFixture.Generated.g.cs",
                SourceText.From(
                    "namespace Fixture.App; internal static class GeneratedMarker { public const string Value = \"YAAP\"; }",
                    Encoding.UTF8));
        });
    }
}
