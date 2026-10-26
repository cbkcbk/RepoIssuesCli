using RepoCli.Core.Features.Anchors;

namespace RepoCli.Core.Tests.Features.Anchors;

public class SymbolIndexTests
{
    const string CSharp = """
        namespace Demo;

        public class Outer
        {
            private readonly int count;

            public Outer(int count) : base()
            {
                this.count = count;
            }

            public class Inner
            {
                public async Task<int> RunAsync(string name)
                {
                    if (name.Length > 0)
                    {
                        return 1;
                    }
                    return 0;
                }
            }

            public int Twice(int value) => value * 2;

            public record Point(int X, int Y);
        }
        """;

    static SymbolIndex Index(string source) => SymbolIndex.Scan(SourceText.Parse(source));

    static (string Symbol, SymbolKind Kind, int Line, int EndLine)[] Shape(string source) =>
        [.. Index(source).Declarations.Select(declaration => (declaration.Symbol, declaration.Kind, declaration.Line, declaration.EndLine))];

    [Test]
    public void CSharpNestedTypesAndMembers() =>
        Assert.That(Shape(CSharp), Is.EqualTo(new[]
        {
            ("Outer", SymbolKind.Class, 3, 27),
            ("Outer.Outer", SymbolKind.Method, 7, 10),
            ("Outer.Inner", SymbolKind.Class, 12, 22),
            ("Outer.Inner.RunAsync", SymbolKind.Method, 14, 21),
            ("Outer.Twice", SymbolKind.Method, 24, 24),
            ("Outer.Point", SymbolKind.Record, 26, 26)
        }));

    [TestCase(18, "Outer.Inner.RunAsync")]
    [TestCase(16, "Outer.Inner.RunAsync")]
    [TestCase(13, "Outer.Inner")]
    [TestCase(5, "Outer")]
    [TestCase(24, "Outer.Twice")]
    public void EnclosingDeclarationIsTheInnermost(int line, string symbol) =>
        Assert.That(Index(CSharp).SymbolAt(line), Is.EqualTo(symbol));

    [Test]
    public void LineOutsideAnyDeclarationHasNoSymbol() =>
        Assert.Multiple(() =>
        {
            Assert.That(Index(CSharp).EnclosingAt(1), Is.Null);
            Assert.That(Index(CSharp).SymbolAt(2), Is.Null);
        });

    [Test]
    public void ControlFlowKeywordsAreNotMethods() =>
        Assert.That(Index(CSharp).Declarations.Select(declaration => declaration.Name), Has.None.AnyOf("if", "return"));

    [Test]
    public void ExpressionBodiedMemberSpanningLines()
    {
        const string source = """
            public class Calc
            {
                public int Sum(int a, int b) =>
                    a + b;

                public int Next() => 1;

                public (int Low, int High) Range() => (1, 2);
            }
            """;

        Assert.That(Shape(source), Is.EqualTo(new[]
        {
            ("Calc", SymbolKind.Class, 1, 9),
            ("Calc.Sum", SymbolKind.Method, 3, 4),
            ("Calc.Next", SymbolKind.Method, 6, 6),
            ("Calc.Range", SymbolKind.Method, 8, 8)
        }));
    }

    [Test]
    public void BracesInsideStringsAndCommentsAreIgnored()
    {
        const string source = """"
public class Strings
{
    public string Open() => "{";

    public string Close()
    {
        var text = "}}} { \" }";
        var chr = '}';
        var verbatim = @"
} "" {{
";
        // } comment
        /* { */ var raw = """
            }
            """;
        return text;
    }

    public void After() { }
}
"""";

        Assert.That(Shape(source), Is.EqualTo(new[]
        {
            ("Strings", SymbolKind.Class, 1, 20),
            ("Strings.Open", SymbolKind.Method, 3, 3),
            ("Strings.Close", SymbolKind.Method, 5, 17),
            ("Strings.After", SymbolKind.Method, 19, 19)
        }));
    }

    [Test]
    public void TypeScript()
    {
        const string source = """
            export interface Shape {
              area(): number;
            }

            export class Circle implements Shape {
              constructor(private radius: number) {}

              area(): number {
                return Math.PI * this.radius ** 2;
              }

              private handle = (event: Event) => {
                console.log(event);
              };
            }

            export function describe(shape: Shape): string {
              return `area ${shape.area()} }`;
            }

            const double = (x: number) => x * 2;

            export default async function load<T>(url: string): Promise<T> {
              const response = await fetch(url);
              return response.json();
            }
            """;

        Assert.That(Shape(source), Is.EqualTo(new[]
        {
            ("Shape", SymbolKind.Interface, 1, 3),
            ("Circle", SymbolKind.Class, 5, 15),
            ("Circle.constructor", SymbolKind.Method, 6, 6),
            ("Circle.area", SymbolKind.Method, 8, 10),
            ("Circle.handle", SymbolKind.Method, 12, 14),
            ("describe", SymbolKind.Function, 17, 19),
            ("double", SymbolKind.Function, 21, 21),
            ("load", SymbolKind.Function, 23, 26)
        }));
    }

    [Test]
    public void CallsWithCallbacksAreNotDeclarations()
    {
        const string source = """
            function outer() {
              it("works", () => {
                expect(1).toBe(1);
              });
              items.forEach(function (item) {
                run(item);
              });
            }
            """;

        Assert.Multiple(() =>
        {
            Assert.That(Shape(source), Is.EqualTo(new[] { ("outer", SymbolKind.Function, 1, 8) }));
            Assert.That(Index(source).SymbolAt(3), Is.EqualTo("outer"));
        });
    }

    [Test]
    public void Python()
    {
        const string source = """"
            import os

            class Repository(Base):
                """Docs with { braces
            and a dedented line."""

                def __init__(self, path):
                    self.path = path

                class Entry:
                    def describe(self) -> str:
                        if self.path:
                            return "x"
                        return "y"

                async def load(
                    self,
                    name,
                ):
                    data = {
                "key": 1,
                    }
                    return data

            def helper():
                # comment
            # dedented comment
                return 1

            print(helper())
            """";

        Assert.Multiple(() =>
        {
            Assert.That(Shape(source), Is.EqualTo(new[]
            {
                ("Repository", SymbolKind.Class, 3, 23),
                ("Repository.__init__", SymbolKind.Method, 7, 8),
                ("Repository.Entry", SymbolKind.Class, 10, 14),
                ("Repository.Entry.describe", SymbolKind.Method, 11, 14),
                ("Repository.load", SymbolKind.Method, 16, 23),
                ("helper", SymbolKind.Function, 25, 28)
            }));
            Assert.That(Index(source).SymbolAt(30), Is.Null);
        });
    }

    [Test]
    public void Go()
    {
        const string source = """
            package server

            type Server struct {
                name string
            }

            type Handler interface {
                Handle(w Writer) error
            }

            func (s *Server) Start(port int) error {
                for i := 0; i < 3; i++ {
                    fmt.Println("{")
                }
                return nil
            }

            func New[T any](name string) *Server {
                return &Server{name: name}
            }
            """;

        Assert.That(Shape(source), Is.EqualTo(new[]
        {
            ("Server", SymbolKind.Struct, 3, 5),
            ("Handler", SymbolKind.Interface, 7, 9),
            ("Server.Start", SymbolKind.Method, 11, 16),
            ("New", SymbolKind.Function, 18, 20)
        }));
    }

    [Test]
    public void Rust()
    {
        const string source = """
            pub struct Point {
                x: i32,
            }

            pub trait Shape {
                fn area(&self) -> f64;
            }

            impl<T: Into<f64>> Shape for Wrapper<T> {
                fn area(&self) -> f64 {
                    match self.kind {
                        Some(x) => { x }
                        _ => 0.0,
                    }
                }
            }

            pub fn longest<'a>(a: &'a str, b: &'a str) -> &'a str {
                let brace = '{';
                if a.len() > b.len() { a } else { b }
            }
            """;

        Assert.That(Shape(source), Is.EqualTo(new[]
        {
            ("Point", SymbolKind.Struct, 1, 3),
            ("Shape", SymbolKind.Trait, 5, 7),
            ("Shape.area", SymbolKind.Method, 6, 6),
            ("Wrapper", SymbolKind.Impl, 9, 16),
            ("Wrapper.area", SymbolKind.Method, 10, 15),
            ("longest", SymbolKind.Function, 18, 21)
        }));
    }

    [Test]
    public void Java()
    {
        const string source = """
            package demo;

            @Service
            public class OrderService extends Base implements Api {
                @Override
                public List<Order> findAll(int page) throws IOException {
                    synchronized (this) {
                        return repo.find("}");
                    }
                }

                private static <T> T first(List<T> items)
                {
                    try {
                        return items.get(0);
                    } catch (Exception e) {
                        return null;
                    }
                }

                enum Status { ACTIVE, CLOSED }
            }
            """;

        Assert.That(Shape(source), Is.EqualTo(new[]
        {
            ("OrderService", SymbolKind.Class, 4, 22),
            ("OrderService.findAll", SymbolKind.Method, 6, 10),
            ("OrderService.first", SymbolKind.Method, 12, 19),
            ("OrderService.Status", SymbolKind.Enum, 21, 21)
        }));
    }

    [TestCase("Outer.Inner.RunAsync", 14)]
    [TestCase("Renamed.Inner.RunAsync", 14)]
    [TestCase("RunAsync", 14)]
    [TestCase("Inner", 12)]
    public void FindMatchesExactThenTrailingSegments(string symbol, int line) =>
        Assert.That(Index(CSharp).Find(symbol).Select(declaration => declaration.Line), Is.EqualTo(new[] { line }));

    [Test]
    public void FindPrefersExactMatchAndReturnsOverloads()
    {
        const string source = """
            class A
            {
                void Run() { }
                void Run(int x) { }
                class B
                {
                    void Run() { }
                }
            }
            """;
        var index = Index(source);

        Assert.Multiple(() =>
        {
            Assert.That(index.Find("A.Run").Select(declaration => declaration.Line), Is.EqualTo(new[] { 3, 4 }));
            Assert.That(index.Find("Run").Select(declaration => declaration.Line), Is.EqualTo(new[] { 3, 4, 7 }));
            Assert.That(index.Find("Missing"), Is.Empty);
        });
    }

    [Test]
    public void CreateWithSymbolFillsTheEnclosingSymbol()
    {
        var text = SourceText.Parse(CSharp);

        Assert.Multiple(() =>
        {
            Assert.That(Anchor.CreateWithSymbol("Outer.cs", text, 18, 18).Symbol, Is.EqualTo("Outer.Inner.RunAsync"));
            Assert.That(Anchor.CreateWithSymbol("Outer.cs", text, 2).Symbol, Is.EqualTo("Outer"));
            Assert.That(Anchor.CreateWithSymbol("Outer.cs", text, 1).Symbol, Is.Null);
        });
    }
}
