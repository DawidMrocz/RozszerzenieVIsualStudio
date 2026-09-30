using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mroczware.PowerFullTool.Refactoring
{
    public class PropertyInfo
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public bool IsSerialized { get; set; }
    }

    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(ParamsRefactoring)), Shared]
    internal class ParamsRefactoring : CodeRefactoringProvider
    {
        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            SyntaxNode root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            SyntaxNode node = root.FindNode(context.Span);
            MethodDeclarationSyntax methodDecl = node as MethodDeclarationSyntax
                    ?? node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();

            if (methodDecl is not null && methodDecl.ExpressionBody is null)
            {
                ClassDeclarationSyntax classParent = methodDecl.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                InterfaceDeclarationSyntax interfaceParent = methodDecl.Ancestors().OfType<InterfaceDeclarationSyntax>().FirstOrDefault();

                if (interfaceParent is null && classParent is not null && classParent.Identifier.Text.Contains("Repository"))
                {
                    CodeAction action = CodeAction.Create(
                  $"🚀 Dodaj Dapper params w metodzie {methodDecl.Identifier.Text}",
                  async c => await CreateParamsAsync(context, methodDecl, c));

                    context.RegisterRefactoring(action);
                }
            }
        }

        /// <summary>
        /// CreateMapping
        /// </summary>
        /// <param name="context"></param>
        /// <param name="methodDecl"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<Document> CreateParamsAsync(CodeRefactoringContext context, MethodDeclarationSyntax methodDecl, CancellationToken cancellationToken)
        {
            Compilation compilation = await context.Document.Project.GetCompilationAsync();
            SyntaxTree syntaxTree = await context.Document.GetSyntaxTreeAsync();
            SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);
            IMethodSymbol methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
            ITypeSymbol returnType = methodSymbol.ReturnType;
            string unwrappedType = ExtractInnermostType(returnType);
            List<PropertyInfo> result = [];

            bool addLinQ = false;

            foreach (ParameterSyntax param in methodDecl.ParameterList.Parameters)
            {
                TypeSyntax typeSyntax = param.Type;
                if (typeSyntax == null) continue;

                Microsoft.CodeAnalysis.TypeInfo typeInfo = semanticModel.GetTypeInfo(typeSyntax);
                INamedTypeSymbol typeSymbol = typeInfo.Type as INamedTypeSymbol;

                string? typeString = typeInfo.Type?.ToString().Replace("?", "");

                if (!string.IsNullOrWhiteSpace(typeString) && IsKnownDotNetType(typeString))
                {
                    result.Add(new PropertyInfo()
                    {
                        Name = CapitalizeFirstLetter(param.Identifier.Text),
                        Value = param.Identifier.Text,
                    });

                }
                else if (!string.IsNullOrWhiteSpace(typeString) && typeString.Contains("System.Collections.Generic"))
                {
                    var generateParamWay = AssessGenerateParamWay(typeSymbol);

                    if (generateParamWay == ParamGenerateWay.Serialize)
                    {
                        result.Add(new PropertyInfo()
                        {
                            Name = $"{CapitalizeFirstLetter(param.Identifier.Text)}",
                            Value = $"System.Text.Json.JsonSerializer.Serialize({param.Identifier.Text})",
                            IsSerialized = true
                        });
                    }
                    else
                    {
                        result.Add(new PropertyInfo()
                        {
                            Name = $"{CapitalizeFirstLetter(param.Identifier.Text)}",
                            Value = $"({param.Identifier.Text} != null ? string.Join(\";\", {param.Identifier.Text}{ConditionForFilterElementsForStringJoin(typeSymbol)}.Select(x => x.ToString())) : string.Empty)",
                            IsSerialized = false
                        });

                        addLinQ = true;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(typeString) && IsKnownDotNetArrayType(typeString))
                {

                    string whereCondition = "";

                    if (typeString == "int[]")
                        whereCondition = $".Where(x => x != null && x != 0)";
                    else if (typeString == "System.Guid[]" || typeString == "Guid[]")
                        whereCondition = $".Where(x => x != null && x != Guid.Empty)";
                    else if (typeString == "string[]")
                        whereCondition = $".Where(x => !string.IsNullOrWhiteSpace(x))";
                    else if (typeString == "System.DateTime[]" || typeString == "DateTime[]")
                        whereCondition = $".Where(x => x != null && x != DateTime.MinValue)";
                    else
                        whereCondition = $".Where(x => x != null)";


                    result.Add(new PropertyInfo()
                    {
                        Name = $"{CapitalizeFirstLetter(param.Identifier.Text)}",
                        Value = $"({param.Identifier.Text} != null ? string.Join(\";\", {param.Identifier.Text}{whereCondition}.Select(x => x.ToString())) : string.Empty)",
                    });

                    addLinQ = true;
                }
                else if (typeSymbol != null && !string.IsNullOrWhiteSpace(typeString))
                {
                    IParameterSymbol parameterSymbol = semanticModel.GetDeclaredSymbol(param);
                    INamedTypeSymbol actualTypeSymbol = parameterSymbol?.Type as INamedTypeSymbol;

                    if (actualTypeSymbol is null) continue;
                    List<IPropertySymbol> props = GetAllPropertiesWithPublicGetter(actualTypeSymbol);
                    IEnumerable<PropertyInfo> properties = props
                        .Where(p => p.DeclaredAccessibility == Accessibility.Public)
                        .Select(p =>
                            {
                                var generateParamWay = AssessGenerateParamWay(p.Type);

                                if (generateParamWay == ParamGenerateWay.StringJoin)
                                    addLinQ = true;

                                return new PropertyInfo()
                                {

                                    Name = CapitalizeFirstLetter(p.Name),
                                    IsSerialized = generateParamWay == ParamGenerateWay.Serialize ? true : false,
                                    Value = generateParamWay switch
                                    {
                                        ParamGenerateWay.Serialize => $"System.Text.Json.JsonSerializer.Serialize({param.Identifier.Text}.{p.Name})",
                                        ParamGenerateWay.StringJoin => $"({param.Identifier.Text}.{p.Name} != null ? string.Join(\";\", {param.Identifier.Text}.{p.Name}{ConditionForFilterElementsForStringJoin(p.Type)}.Select(x => x.ToString())) : string.Empty)",
                                        ParamGenerateWay.Simple => $"{param.Identifier.Text}.{p.Name}",
                                        _ => $"{param.Identifier.Text}.{p.Name}"
                                    }
                                };
                            }
                        ).ToList();

                    result.AddRange(properties);
                }
            }

            StringBuilder exampleInitialization = new();

            if (result.Any())
            {
                exampleInitialization.AppendLine("             var _params = new DynamicParameters();");

                foreach (PropertyInfo prop in result)
                {
                    string key = prop.IsSerialized ? $"{prop.Name}Json" : prop.Name;
                    exampleInitialization.AppendLine($"            _params.Add(\"{key}\", {prop.Value});");
                }
            }

            bool methodHasBody = false;

            if (methodDecl.Body != null)
                methodHasBody = methodDecl.Body.Statements.Count > 0;

            IReadOnlyList<string> folders = context.Document.Folders;
            string resourceName = $"Sql.{string.Join("_", folders.Skip(1))}_{methodSymbol.Name}";

            if (!methodHasBody)
            {
                Match match = Regex.Match(methodSymbol.Name, @"\b(Create|Update|Delete|Search|Get|GetList|GetById|GetByIdAsync|CreateAsync|UpdateAsync|DeleteAsync|SearchAsync|GetAsync|GetListAsync)\b", RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    exampleInitialization.AppendLine($"");
                    var suffix = result.Any() ? ",_params" : "";
                    string matchedWord = match.Value.ToLower();

                    switch (matchedWord)
                    {
                        case "create":
                        case "createasync":
                            exampleInitialization.AppendLine($"            var context = new DatabaseContext({resourceName}, System.Transactions.TransactionScopeOption.Required, System.Data.CommandType.Text{suffix});");
                            exampleInitialization.AppendLine($"            return await _databaseService.GetValueAsync<int>(context);");
                            break;
                        case "delete":
                        case "deleteasync":
                        case "update":
                        case "updateasync":
                            exampleInitialization.AppendLine($"            var context = new DatabaseContext({resourceName}, System.Transactions.TransactionScopeOption.Required, System.Data.CommandType.Text{suffix});");
                            exampleInitialization.AppendLine($"            await _databaseService.ExecuteNoResultAsync(context);");
                            break;
                        case "get":
                        case "getasync":
                        case "getbyid":
                        case "getbyidasync":
                            exampleInitialization.AppendLine($"            var context = new DatabaseContext({resourceName}, System.Transactions.TransactionScopeOption.Required, System.Data.CommandType.Text{suffix});");
                            exampleInitialization.AppendLine($"            return (await _databaseService.QueryAsync<{unwrappedType}>(context)).FirstOrDefault();");
                            break;
                        case "search":
                        case "searchasync":
                        case "getlist":
                        case "getlistasync":
                            exampleInitialization.AppendLine($"            var context = new DatabaseContext({resourceName}, System.Transactions.TransactionScopeOption.Required, System.Data.CommandType.Text{suffix});");
                            exampleInitialization.AppendLine($"            return await _databaseService.QueryAsync<{unwrappedType}>(context);");
                            break;
                        default:
                            break;
                    }
                }
            }

            var statement = SyntaxFactory.ParseStatement(exampleInitialization.ToString());

            // Wstawienie instrukcji na początek ciała metody
            var newStatements = methodDecl.Body.Statements.Insert(0, statement);
            var newBody = methodDecl.Body.WithStatements(newStatements);

            var newMethodDeclaration = methodDecl.WithBody(newBody);
            CompilationUnitSyntax root = await context.Document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;

            var newRoot = root.ReplaceNode(methodDecl, newMethodDeclaration) as CompilationUnitSyntax;

            //Dodanei using'a Dapper
            if (!newRoot.Usings.Any(u => u.Name.ToString() == $"Dapper"))
            {
                UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName($"Dapper"));

                newRoot = newRoot.AddUsings(newUsingDirective);
            }

            //Dodanei using'a z DatabaseContext
            if (!newRoot.Usings.Any(u => u.Name.ToString() == $"Mroczware.DataBase.Models"))
            {
                UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName($"Mroczware.DataBase.Models"));

                newRoot = newRoot.AddUsings(newUsingDirective);
            }

            //Dodaneie linQ
            if (!newRoot.Usings.Any(u => u.Name.ToString() == $"System.Linq") && addLinQ)
            {
                UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName($"System.Linq"));

                newRoot = newRoot.AddUsings(newUsingDirective);
            }

            //Dodanei using'a z Resource
            if (!newRoot.Usings.Any(u => u.Name.ToString() == $"{context.Document.Project.Name}.Resources.{string.Join(".", folders.Skip(1))}"))
            {
                UsingDirectiveSyntax newUsingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName($"{context.Document.Project.Name}.Resources.{string.Join(".", folders.Skip(1))}"));

                newRoot = newRoot.AddUsings(newUsingDirective);
            }

            return context.Document.WithSyntaxRoot(newRoot);
        }

        static string ExtractInnermostType(ITypeSymbol typeSymbol)
        {
            while (typeSymbol is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
                typeSymbol = namedType.TypeArguments[0];

            return typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }

        public static bool IsKnownDotNetType(string typeName)
        {
            HashSet<string> knownTypes =
            [
                "System.TimeSpan",
                "int",
                "string",
                "bool",
                "byte",
                "sByte",
                "short",
                "uint",
                "long",
                "float",
                "double",
                "decimal",
                "char",
                "object",
                "System.DateTime",
                "System.Guid",
                "Guid",
                "DateTime",
            ];

            return knownTypes.Contains(typeName);
        }

        public static bool IsKnownDotNetArrayType(string typeName)
        {
            HashSet<string> knownTypes =
            [
                "string[]",
                "int[]",
                "MyClass[]",
                "DateTime?[]",
                "bool[]",
                "byte[]",
                "sbyte[]",
                "short[]",
                "ushort[]",
                "long[]",
                "ulong[]",
                "float[]",
                "double[]",
                "decimal[]",
                "char[]",
                "System.Guid[]",
                "System.DateTime[]",
            ];

            return knownTypes.Contains(typeName);
        }

        public static ParamGenerateWay AssessGenerateParamWay(ITypeSymbol input)
        {
            if (string.IsNullOrEmpty(input.ToString()))
                return ParamGenerateWay.Simple;

            string[] genericTypes = new[]
            {
                "IEnumerable<",
                "ICollection<",
                "IList<",
                "List<",
                "ObservableCollection<",
                "Collection<",
                "HashSet<",
                "LinkedList<",
                "Queue<",
                "Stack<"
            };

            string innerType = ExtractInnermostType(input);
            bool isSimpleType = IsKnownDotNetType(innerType);
            bool isGeneric = genericTypes.Any(type => input.ToString().Contains(type));

            if (isSimpleType && !isGeneric)
                return ParamGenerateWay.Simple;
            else if (!isSimpleType && isGeneric)
                return ParamGenerateWay.Serialize;
            else if (isSimpleType && isGeneric)
                return ParamGenerateWay.StringJoin;
            else
                return ParamGenerateWay.Simple;
        }

        public static string ConditionForFilterElementsForStringJoin(ITypeSymbol input)
        {
            string innerType = ExtractInnermostType(input);

            if (innerType == "int")
                return $".Where(x => x != null && x != 0)";
            else if (innerType == "System.Guid" || innerType == "Guid")
                return $".Where(x => x != null && x != Guid.Empty)";
            else if (innerType == "string")
                return $".Where(x => !string.IsNullOrWhiteSpace(x))";
            else if (innerType == "System.DateTime" || innerType == "DateTime")
                return $".Where(x => x != null && x != DateTime.MinValue)";
            else
                return $".Where(x => x != null)";
        }

        public enum ParamGenerateWay
        {
            Simple,
            StringJoin,
            Serialize
        }

        private static string CapitalizeFirstLetter(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            for (int i = 0; i < input.Length; i++)
                if (char.IsLetter(input[i]))
                    return input.Substring(0, i) +
                               char.ToUpper(input[i]) +
                               input.Substring(i + 1);

            return input; // brak liter
        }

        private static List<IPropertySymbol> GetAllPropertiesWithPublicGetter(INamedTypeSymbol typeSymbol)
        {
            List<IPropertySymbol> result = [];

            void Collect(INamedTypeSymbol symbol)
            {
                if (symbol == null || symbol.Name == "Object") return;

                IEnumerable<IPropertySymbol> properties = symbol.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p =>
                    p.DeclaredAccessibility == Accessibility.Public
                    && p.GetMethod != null
                    && p.GetMethod.DeclaredAccessibility == Accessibility.Public
                    && !p.IsStatic && !result.Any(r => r.Name == p.Name));

                result.AddRange(properties);

                Collect(symbol.BaseType);
            }

            Collect(typeSymbol);
            return result;
        }
    }
}
