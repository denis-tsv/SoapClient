using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SoapClientGenerator.Roslyn
{
    public class CollectMetadataSyntaxWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _semanticModel;

        public readonly List<INamedTypeSymbol> Services = new List<INamedTypeSymbol>();
        public readonly List<INamedTypeSymbol> Contracts = new List<INamedTypeSymbol>();
        public readonly List<INamedTypeSymbol> Enums = new List<INamedTypeSymbol>();

        public CollectMetadataSyntaxWalker(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel;
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            Enums.Add(_semanticModel.GetDeclaredSymbol(node));
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            var symbol = _semanticModel.GetDeclaredSymbol(node);
            if (symbol.GetAttributes().Any(attr => attr.AttributeClass.Name == "ServiceContractAttribute"))
            {
                Services.Add(symbol);
            }
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var symbol = _semanticModel.GetDeclaredSymbol(node);
            if (symbol.GetAttributes().Any(attr =>attr.AttributeClass.Name == "DataContractAttribute" || attr.AttributeClass.Name == "MessageContractAttribute"))
            {
                Contracts.Add(symbol);
            }
        }
    }
}
