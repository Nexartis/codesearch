using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ScipCsharp;
using Xunit;

namespace ScipCsharp.Tests;

/// <summary>
/// Identity-collision fixtures for <see cref="SymbolIndexer.SymbolToScipName"/>
/// (todo #139 B4). Each test compiles a small in-memory snippet with REAL
/// Roslyn symbols — no MSBuild, no workspace, no BCL references; the fixture
/// code declares only its own types — and pins the three key-format
/// guarantees: generic arity, full containing-type paths and fully qualified
/// parameter types keep distinct declarations on distinct keys.
/// </summary>
public class KeyFormatTests
{
    /// <summary>
    /// Compiles <paramref name="source"/> and maps every declared named type
    /// and ordinary method through the production key builder.
    /// </summary>
    private static Dictionary<ISymbol, string> BuildKeys(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "keyformat-fixtures",
            [tree],
            references: [],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var keys = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        Collect(compilation.SourceModule.GlobalNamespace, keys);
        Assert.NotEmpty(keys);
        return keys;
    }

    private static void Collect(INamespaceSymbol ns, Dictionary<ISymbol, string> keys)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
                Collect(child, keys);
            else if (member is INamedTypeSymbol type)
                CollectType(type, keys);
        }
    }

    private static void CollectType(INamedTypeSymbol type, Dictionary<ISymbol, string> keys)
    {
        var key = SymbolIndexer.SymbolToScipName(type);
        if (!string.IsNullOrEmpty(key))
            keys[type] = key;

        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol nested)
            {
                CollectType(nested, keys);
            }
            else if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
            {
                var methodKey = SymbolIndexer.SymbolToScipName(method);
                if (!string.IsNullOrEmpty(methodKey))
                    keys[method] = methodKey;
            }
        }
    }

    [Fact]
    public void GenericArityDistinguishesFooFromFooOfT()
    {
        var keys = BuildKeys("""
            namespace Ns;
            class Foo { }
            class Foo<T> { }
            """);

        var plain = keys.Values.Single(k => k == "csharp Ns . Foo#");
        var generic = keys.Values.Single(k => k == "csharp Ns . Foo`1#");
        Assert.NotEqual(plain, generic);
        Assert.Contains("`1", generic);
    }

    [Fact]
    public void GenericArityDistinguishesMethodOverloads()
    {
        var keys = BuildKeys("""
            namespace Ns;
            class C
            {
                void M() { }
                void M<T>() { }
            }
            """);

        var plain = keys.Values.Single(k => k == "csharp Ns . C#M().");
        var generic = keys.Values.Single(k => k == "csharp Ns . C#M`1().");
        Assert.NotEqual(plain, generic);
        // Arity sits between the name and the parameter list.
        Assert.Contains("#M`1(", generic);
    }

    [Fact]
    public void NestedTypesCarryTheirFullContainingPath()
    {
        var keys = BuildKeys("""
            namespace Ns;
            class Outer1 { class Inner { void M() { } } }
            class Outer2 { class Inner { void M() { } } }
            """);

        var o1 = keys.Values.Single(k => k == "csharp Ns . Outer1.Inner#M().");
        var o2 = keys.Values.Single(k => k == "csharp Ns . Outer2.Inner#M().");
        Assert.NotEqual(o1, o2);
    }

    [Fact]
    public void ParameterTypesAreFullyQualified()
    {
        var keys = BuildKeys("""
            namespace A { class P { } }
            namespace B { class P { } }
            namespace Ns
            {
                class C
                {
                    void M(A.P x) { }
                    void M(B.P y) { }
                }
            }
            """);

        var fromA = keys.Values.Single(k => k == "csharp Ns . C#M(A.P).");
        var fromB = keys.Values.Single(k => k == "csharp Ns . C#M(B.P).");
        Assert.NotEqual(fromA, fromB);
        // No bare-param collapse left behind by the qualification.
        Assert.DoesNotContain("(P ", fromA);
        Assert.DoesNotContain(", P)", fromA);
        Assert.DoesNotContain("(P ", fromB);
        Assert.DoesNotContain(", P)", fromB);
    }

    [Fact]
    public void RefKindsAreStillDistinguished()
    {
        var keys = BuildKeys("""
            namespace A { class P { } }
            namespace Ns
            {
                class C
                {
                    void M(A.P x) { }
                    void M(ref A.P x) { }
                }
            }
            """);

        // Regression guard: the RefKind switch must survive the switch to the
        // fully qualified parameter format.
        Assert.Equal(
            ["csharp Ns . C#M(A.P).", "csharp Ns . C#M(ref A.P)."],
            keys.Values.Where(k => k.Contains("#M(")).OrderBy(k => k, StringComparer.Ordinal).ToList());
    }
}
