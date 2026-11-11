using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parley.Tools;
using Xunit;

namespace Parley.Tests;

/// <summary>
/// The engine has to compile under UdonSharp inside VRChat, which accepts only part of
/// C#. We cannot run the UdonSharp compiler here, so this test reads the engine source
/// and fails if it uses anything outside a deliberately conservative subset.
/// </summary>
public class UdonSubsetTests
{
    public static IEnumerable<object[]> EngineFiles()
    {
        var dir = Path.Combine(Game.RepoRoot(), "engine", "Parley.Engine");
        foreach (var f in Directory.GetFiles(dir, "*.cs"))
            yield return new object[] { Path.GetFileName(f) };
    }

    private static readonly HashSet<string> AllowedTypes = new()
    {
        "int", "bool", "string", "char", "float", "void", "int[]", "bool[]", "string[]", "char[]", "float[]",
    };

    [Theory]
    [MemberData(nameof(EngineFiles))]
    public void EngineFileStaysInsideTheSubset(string file)
    {
        var path = Path.Combine(Game.RepoRoot(), "engine", "Parley.Engine", file);
        var engineClasses = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.cs").Select(Path.GetFileNameWithoutExtension).ToHashSet();
        var problems = FindProblems(File.ReadAllText(path), file, engineClasses);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void TheCheckItselfCatchesForbiddenCode()
    {
        const string bad = @"
using System.Collections.Generic;
using UdonSharp;
public class Thing : UdonSharpBehaviour {
    public static int counter;
    public List<int> items;
    public int Total { get { return 1; } }
    public int Fact(int n) { return n <= 1 ? 1 : n * Fact(n - 1); }
    public void Go(out int x) { x = 0; try { var y = new Thing(); } catch { throw; } foreach (int i in new int[2]) { } }
}";
        var problems = string.Join("\n", FindProblems(bad, "bad.cs", new HashSet<string> { "Thing" }));
        foreach (var expected in new[] { "using System.Collections.Generic", "static field", "generic", "property", "recursion", "ref/out", "try/catch", "throw", "var", "new Thing", "foreach" })
            Assert.Contains(expected, problems);
    }

    private static List<string> FindProblems(string source, string file, HashSet<string> engineClasses)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp7_3));
        var root = tree.GetRoot();
        var problems = new List<string>();
        void Bad(SyntaxNode n, string what) => problems.Add($"{file}:{n.GetLocation().GetLineSpan().StartLinePosition.Line + 1} {what}");

        foreach (var n in root.DescendantNodes())
        {
            switch (n)
            {
                case TryStatementSyntax: Bad(n, "try/catch"); break;
                case ThrowStatementSyntax: case ThrowExpressionSyntax: Bad(n, "throw"); break;
                case GenericNameSyntax: Bad(n, "generic type or method"); break;
                case TypeParameterListSyntax: Bad(n, "generic declaration"); break;
                case InterfaceDeclarationSyntax: Bad(n, "interface"); break;
                case StructDeclarationSyntax: Bad(n, "struct"); break;
                case EnumDeclarationSyntax: Bad(n, "enum (use int constants)"); break;
                case DelegateDeclarationSyntax: Bad(n, "delegate"); break;
                case LambdaExpressionSyntax: case AnonymousMethodExpressionSyntax: Bad(n, "lambda"); break;
                case QueryExpressionSyntax: Bad(n, "LINQ query"); break;
                case LocalFunctionStatementSyntax: Bad(n, "local function"); break;
                case ForEachStatementSyntax: Bad(n, "foreach (use for)"); break;
                case PropertyDeclarationSyntax: Bad(n, "property (use fields and methods)"); break;
                case IndexerDeclarationSyntax: Bad(n, "indexer"); break;
                case OperatorDeclarationSyntax: case ConversionOperatorDeclarationSyntax: Bad(n, "operator overload"); break;
                case ConstructorDeclarationSyntax: Bad(n, "constructor (use an Init method)"); break;
                case EventDeclarationSyntax: case EventFieldDeclarationSyntax: Bad(n, "event"); break;
                case GotoStatementSyntax: Bad(n, "goto"); break;
                case YieldStatementSyntax: Bad(n, "yield"); break;
                case AwaitExpressionSyntax: Bad(n, "await"); break;
                case NullableTypeSyntax: Bad(n, "nullable type"); break;
                case TupleExpressionSyntax: case TupleTypeSyntax: Bad(n, "tuple"); break;
                case InterpolatedStringExpressionSyntax: Bad(n, "string interpolation (use +)"); break;
                case IsPatternExpressionSyntax: case SwitchExpressionSyntax: Bad(n, "pattern matching"); break;
                case UnsafeStatementSyntax: case FixedStatementSyntax: Bad(n, "unsafe code"); break;
                case LockStatementSyntax: Bad(n, "lock"); break;
                case UsingStatementSyntax: Bad(n, "using statement"); break;
                case ConditionalAccessExpressionSyntax: Bad(n, "?. operator"); break;
                case RefExpressionSyntax: Bad(n, "ref"); break;
                case LocalDeclarationStatementSyntax l when l.IsConst: Bad(n, "local const (use a class const)"); break;
                case ParameterSyntax p when p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.ParamsKeyword) || m.IsKind(SyntaxKind.InKeyword)):
                    Bad(n, "ref/out/params parameter"); break;
                case ArgumentSyntax a when !a.RefKindKeyword.IsKind(SyntaxKind.None): Bad(n, "ref/out argument"); break;
                case ArrayTypeSyntax at when at.RankSpecifiers.Count > 1 || at.RankSpecifiers.Any(r => r.Rank > 1): Bad(n, "jagged or multi-dimensional array"); break;
                case ObjectCreationExpressionSyntax oc:
                    {
                        var t = oc.Type.ToString();
                        if (t != "string") Bad(n, "new " + t + " (engine objects are wired together, never created with new)");
                        break;
                    }
                case UsingDirectiveSyntax u:
                    if (u.Name?.ToString() != "UdonSharp") Bad(n, "using " + u.Name + " (only UdonSharp is allowed)");
                    break;
                case FieldDeclarationSyntax f:
                    if (f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) && !f.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))) Bad(n, "static field");
                    CheckType(f.Declaration.Type);
                    break;
                case MethodDeclarationSyntax m:
                    if (m.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword))) Bad(n, "static method");
                    CheckType(m.ReturnType);
                    foreach (var p in m.ParameterList.Parameters) if (p.Type != null) CheckType(p.Type);
                    break;
                case LocalDeclarationStatementSyntax l:
                    CheckType(l.Declaration.Type);
                    break;
                case ClassDeclarationSyntax c:
                    if (c.BaseList == null || c.BaseList.Types.Count != 1 || c.BaseList.Types[0].ToString() != "UdonSharpBehaviour")
                        Bad(n, "class must derive from UdonSharpBehaviour and nothing else");
                    if (c.Parent is ClassDeclarationSyntax) Bad(n, "nested class");
                    break;
                case IdentifierNameSyntax id when id.Identifier.Text == "var": Bad(n, "var (spell the type out)"); break;
            }
        }

        // recursion: a method that calls itself directly
        foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            foreach (var call in m.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (call.Expression is IdentifierNameSyntax callee && callee.Identifier.Text == m.Identifier.Text)
                    Bad(call, "recursion in " + m.Identifier.Text);

        void CheckType(TypeSyntax t)
        {
            var s = t.ToString();
            if (AllowedTypes.Contains(s) || engineClasses.Contains(s)) return;
            Bad(t, "type " + s + " is outside the allowed set");
        }

        return problems;
    }
}
