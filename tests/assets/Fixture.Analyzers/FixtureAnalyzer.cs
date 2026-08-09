using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fixture.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FixtureAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "YAAPF001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Fixture class",
        "Class '{0}' was analyzed by the YAAP fixture",
        "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType);
    }

    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        if (context.Symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } type)
        {
            System.Threading.Thread.SpinWait(20_000);
            context.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations[0], type.Name));
        }
    }
}
