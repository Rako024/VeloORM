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

    private static string RunGeneratorSources(string body)
    {
        var source = Preamble + "\npublic static class Caller { public static void Run(MyCtx db) {\n" + body + "\n} }";
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(System.Reflection.Assembly a) { if (!a.IsDynamic && !string.IsNullOrEmpty(a.Location)) locations.Add(a.Location); }
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) Add(a);
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
        var compilation = CSharpCompilation.Create("GenTest",
            new[] { CSharpSyntaxTree.ParseText(source) },
            locations.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new VeloInterceptorGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        return string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()));
    }

    [Fact]
    public void Parameterized_QueryCompile_Generates_Compiled_Interceptor()
    {
        var sources = RunGeneratorSources(
            "var q = Query.Compile<MyCtx, int, System.Collections.Generic.List<Foo>>((c, x) => c.Set<Foo>().Where(f => f.X > x).ToList());");
        Assert.Contains("CompiledIntercept_0", sources);
        Assert.Contains("ExecuteListBound", sources);
        Assert.Contains("sink.Add<int>(vp0)", sources); // typed, boxing-free binding
    }

    [Fact]
    public void Unsupported_QueryCompile_Predicate_Is_Not_Compiled_But_Valid()
    {
        // string.Contains is outside the compiled grammar: no interceptor, and no VELO002 (it IS a query).
        var diagnostics = RunGenerator(
            "var q = Query.Compile<MyCtx, string, System.Collections.Generic.List<Foo>>((c, s) => c.Set<Foo>().Where(f => f.Id.ToString().Contains(s)).ToList());");
        Assert.DoesNotContain(diagnostics, d => d.Id == "VELO002");
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

    [Theory]
    [InlineData("_ = db.Set<Foo>().OrderBy(f => f.X).ToList();")]
    [InlineData("_ = db.Set<Foo>().OrderByDescending(f => f.X).ThenBy(f => f.Id).ToList();")]
    [InlineData("_ = db.Set<Foo>().OrderBy(f => f.X).Skip(5).Take(10).ToList();")]
    [InlineData("_ = db.Set<Foo>().Distinct().ToList();")]
    [InlineData("_ = db.Set<Foo>().OrderBy(f => f.X).First();")]
    [InlineData("var s = db.Set<Foo>().Sum(f => f.X);")]
    [InlineData("var m = db.Set<Foo>().Max(f => f.X);")]
    public void Static_Operator_Chains_Are_Intercepted_No_VELO001(string body)
    {
        var diagnostics = RunGenerator(body);
        Assert.DoesNotContain(diagnostics, d => d.Id == "VELO001");
    }

    [Theory]
    // A captured value in a predicate cannot be supplied to a (parameterless) interceptor → runtime.
    [InlineData("int t = 3; _ = db.Set<Foo>().Where(f => f.X > t).ToList();")]
    // Non-constant Skip/Take cannot be baked into static SQL → runtime.
    [InlineData("int n = 2; _ = db.Set<Foo>().Skip(n).Take(5).ToList();")]
    // A predicate-bearing aggregate carries a closure → runtime.
    [InlineData("var c = db.Set<Foo>().Count(f => f.X > 0);")]
    // A nested navigation key selector is not statically translatable here → runtime.
    [InlineData("var l = db.Set<Foo>().Select(f => f.X).ToList();")]
    public void Closure_Or_Unsupported_Chains_Fall_To_Runtime_VELO001(string body)
    {
        var diagnostics = RunGenerator(body);
        Assert.Contains(diagnostics, d => d.Id == "VELO001");
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
