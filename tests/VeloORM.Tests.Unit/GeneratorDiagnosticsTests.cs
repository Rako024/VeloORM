using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VeloORM.Generator;

namespace VeloORM.Tests.Unit;

public class GeneratorDiagnosticsTests
{
    private const string Preamble = """
        using System.Linq;
        using System.Collections.Generic;
        using VeloORM.Runtime;

        public class Foo { public int Id { get; set; } public int X { get; set; } }
        public class MyCtx : VeloDbContext
        {
            public MyCtx() : base(null!, null!, null!, null!) { }
        }
        """;

    private static ImmutableArray<Diagnostic> RunGenerator(string body)
    {
        var source = Preamble + "\npublic static class Caller { public static void Run(MyCtx db) {\n" + body + "\n} }";

        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(System.Reflection.Assembly a)
        {
            if (!a.IsDynamic && !string.IsNullOrEmpty(a.Location)) locations.Add(a.Location);
        }
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) Add(a);
        // Force-load and reference the assemblies the generated/analyzed code needs to bind symbols.
        Add(typeof(object).Assembly);
        Add(typeof(System.Linq.Enumerable).Assembly);
        Add(typeof(System.Linq.Queryable).Assembly);
        Add(typeof(System.Linq.Expressions.Expression).Assembly);
        Add(typeof(VeloORM.Runtime.VeloDbContext).Assembly);
        Add(typeof(VeloORM.Query.SqlParameterBinding).Assembly);
        var coreDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var name in new[] { "netstandard.dll", "System.Runtime.dll", "System.Collections.dll" })
        {
            var p = System.IO.Path.Combine(coreDir, name);
            if (System.IO.File.Exists(p)) locations.Add(p);
        }
        var references = locations.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        var compilation = CSharpCompilation.Create(
            "GenTest",
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new VeloInterceptorGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        return diagnostics;
    }

    [Fact]
    public void Runtime_Fallback_Query_Reports_VELO001()
    {
        var diagnostics = RunGenerator("_ = db.Set<Foo>().Where(f => f.X > 0).ToList();");
        Assert.Contains(diagnostics, d => d.Id == "VELO001");
    }

    [Fact]
    public void Static_WholeTable_Query_Does_Not_Report_VELO001()
    {
        var diagnostics = RunGenerator("_ = db.Set<Foo>().ToList();");
        Assert.DoesNotContain(diagnostics, d => d.Id == "VELO001");
    }

    [Fact]
    public void Valid_QueryCompile_Does_Not_Report_VELO002()
    {
        var diagnostics = RunGenerator("var q = Query.Compile<MyCtx, int>(c => c.Set<Foo>().Count());");
        Assert.DoesNotContain(diagnostics, d => d.Id == "VELO002");
    }

    [Fact]
    public void NonStatic_QueryCompile_Reports_VELO002()
    {
        var diagnostics = RunGenerator("var q = Query.Compile<MyCtx, int>(c => 42);");
        Assert.Contains(diagnostics, d => d.Id == "VELO002");
    }
}
