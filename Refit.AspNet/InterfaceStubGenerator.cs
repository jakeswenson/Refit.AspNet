using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Refit.AspNet
{
    public class InterfaceStubGenerator
    {
        public Tuple<SyntaxTree, IEnumerable<Diagnostic>> GenerateInterfaceStubs(IEnumerable<SyntaxTree> trees)
        {
            var interfacesToGenerate = trees.SelectMany(FindInterfacesToGenerate).ToList();

            var templateInfo = GenerateTemplateInfoForInterfaceList(interfacesToGenerate);

            var diagnostics = GenerateWarnings(interfacesToGenerate);

            return Tuple.Create(RefitClassSyntaxGenerator.GenerateSyntax(templateInfo), diagnostics);
        }

        public List<InterfaceDeclarationSyntax> FindInterfacesToGenerate(SyntaxTree tree)
        {
            var nodes = tree.GetRoot().DescendantNodes().ToList();

            // Make sure this file imports Refit. If not, we're not going to 
            // find any Refit interfaces
            // NB: This falls down in the tests unless we add an explicit "using Refit;",
            // but we can rely on this being there in any other file
            if (nodes.OfType<UsingDirectiveSyntax>().All(u => u.Name.ToFullString() != "Refit"))
                return new List<InterfaceDeclarationSyntax>();

            return nodes.OfType<InterfaceDeclarationSyntax>()
                .Where(i => i.Members.OfType<MethodDeclarationSyntax>().Any(HasRefitHttpMethodAttribute))
                .ToList();
        }

        private static readonly HashSet<string> httpMethodAttributeNames = new HashSet<string>(
            new[] { "Get", "Head", "Post", "Put", "Delete", "Patch" }
                .SelectMany(x => new[] { "{0}", "{0}Attribute" }.Select(f => string.Format(f, x))));

        public bool HasRefitHttpMethodAttribute(MethodDeclarationSyntax method)
        {
            // We could also verify that the single argument is a string, 
            // but what if somebody is dumb and uses a constant?
            // Could be turtles all the way down.
            return method.AttributeLists.SelectMany(a => a.Attributes)
                .Any(a => httpMethodAttributeNames.Contains(a.Name.ToString().Split('.').Last()) &&
                    a.ArgumentList.Arguments.Count == 1 &&
                    a.ArgumentList.Arguments[0].Expression.Kind() == SyntaxKind.StringLiteralExpression);
        }

        public TemplateInformation GenerateTemplateInfoForInterfaceList(List<InterfaceDeclarationSyntax> interfaceList)
        {
            var usings = interfaceList
                .SelectMany(interfaceTree => {
                    var rootNode = interfaceTree.FirstAncestorOrSelf<CompilationUnitSyntax>();

                    return rootNode.DescendantNodes()
                        .OfType<UsingDirectiveSyntax>()
                        .Select(x => string.Format("{0} {1}", x.Alias, x.Name).TrimStart());
                })
                .Distinct()
                .Where(x => x != "System" && x != "System.Net.Http" && x != "System.Collections.Generic" && x != "System.Linq")
                .Select(x => new UsingDeclaration() { Item = x });

            var ret = new TemplateInformation()
            {
                ClassList = interfaceList.Select(x => GenerateClassInfoForInterface(x)).ToList(),
                UsingList = usings.ToList(),
            };

            return ret;
        }

        public ClassTemplateInfo GenerateClassInfoForInterface(InterfaceDeclarationSyntax interfaceTree)
        {
            var ret = new ClassTemplateInfo();
            var parent = interfaceTree.Parent;
            while (parent != null && !(parent is NamespaceDeclarationSyntax)) parent = parent.Parent;

            var ns = parent as NamespaceDeclarationSyntax;
            ret.Namespace = ns.Name.ToString();
            ret.InterfaceName = interfaceTree.Identifier.ValueText;

            if (interfaceTree.TypeParameterList != null)
            {
                var typeParameters = interfaceTree.TypeParameterList.Parameters;
                if (typeParameters.Any())
                {
                    ret.TypeParameters = string.Join(", ", typeParameters.Select(p => p.Identifier.ValueText));
                }
                ret.ConstraintClauses = interfaceTree.ConstraintClauses.ToFullString().Trim();
            }
            ret.MethodList = interfaceTree.Members
                .OfType<MethodDeclarationSyntax>()
                .Select(x => new MethodTemplateInfo()
                {
                    Name = x.Identifier.ValueText,
                    ReturnType = x.ReturnType.ToString(),
                    ArgumentList = string.Join(",", x.ParameterList.Parameters
                        .Select(y => y.Identifier.ValueText)),
                    ArgumentListWithTypes = string.Join(",", x.ParameterList.Parameters
                        .Select(y => string.Format("{0} {1}", y.Type.ToString(), y.Identifier.ValueText))),
                    IsRefitMethod = HasRefitHttpMethodAttribute(x)
                })
                .ToList();

            return ret;
        }

        public IEnumerable<Diagnostic> GenerateWarnings(List<InterfaceDeclarationSyntax> interfacesToGenerate)
        {
            var missingAttributeWarningsDescriptor =
                new DiagnosticDescriptor(
                    nameof(Refit) + ".Warn.01",
                    $"Missing {nameof(Refit)} attribute",
                    $"The method '{{0}}' on interface '{{1}}' is missing a {nameof(Refit)} attribute",
                    nameof(Refit),
                    DiagnosticSeverity.Warning,
                    isEnabledByDefault: true);

            var multipleRefitMethodSameNameWarningDescriptor =
                new DiagnosticDescriptor(
                    nameof(Refit) + ".Warn.02",
                    $"Multiple {nameof(Refit)} methods with the same name",
                    $"Multiple {nameof(Refit)} methods with the same name '{{0}}' on interface '{{1}}'",
                    nameof(Refit),
                    DiagnosticSeverity.Warning,
                    isEnabledByDefault: true);

            var missingAttributeWarnings = interfacesToGenerate
                .SelectMany(i => i.Members.OfType<MethodDeclarationSyntax>().Select(m => new { Interface = i, Method = m }))
                .Where(x => !HasRefitHttpMethodAttribute(x.Method))
                .Select(x => Diagnostic.Create(missingAttributeWarningsDescriptor, x.Method.GetLocation(), x.Method.Identifier, x.Interface.Identifier));

            var overloadWarnings = interfacesToGenerate
                .SelectMany(i => i.Members.OfType<MethodDeclarationSyntax>().Select(m => new { Interface = i, Method = m }))
                .Where(x => HasRefitHttpMethodAttribute(x.Method))
                .GroupBy(x => new { Interface = x.Interface, MethodName = x.Method.Identifier.Text })
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.Select(x =>
                    Diagnostic.Create(multipleRefitMethodSameNameWarningDescriptor, x.Method.GetLocation(), x.Method.Identifier, x.Interface.Identifier)));

            return missingAttributeWarnings.Concat(overloadWarnings);
        }
    }

    public class UsingDeclaration
    {
        public string Item { get; set; }
    }

    public class ClassTemplateInfo
    {
        public string Namespace { get; set; }
        public string InterfaceName { get; set; }
        public string TypeParameters { get; set; }
        public string ConstraintClauses { get; set; }
        public List<MethodTemplateInfo> MethodList { get; set; }
    }

    public class MethodTemplateInfo
    {
        public string ReturnType { get; set; }
        public string Name { get; set; }
        public string ArgumentListWithTypes { get; set; }
        public string ArgumentList { get; set; }
        public bool IsRefitMethod { get; set; }
    }

    public class TemplateInformation
    {
        public List<UsingDeclaration> UsingList { get; set; }
        public List<ClassTemplateInfo> ClassList;
    }
}
